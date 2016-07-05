using System;
using System.IO;
using System.Linq;
using System.Json;
using System.Threading.Tasks;
using Dropbox.Api;

namespace Dropbox_File_Converter
{
    /// <summary>
    /// This class is responsible for all communication between the client and Dropbox's servers. Each instance of this class can support it's own connection.
    /// </summary>
    sealed class DropboxRelay
    {
        /// <summary>
        /// Dropbox App Key, found on the dropbox developer console.
        /// </summary>
        private string dropboxAppKey;
        /// <summary>
        /// Dropbox secret, found on the dropbox developer console.
        /// </summary>
        private string dropboxAppSecret;
        /// <summary>
        /// Dropbox database connection, initialised after the user has obtained their access key.
        /// </summary>
        private DropboxClient dbx;

        /// <summary>
        /// Constructor for the DropboxRelay instance, which will be responsible for all communication between the client and Dropbox's servers.
        /// </summary>
        /// <param name="appKey">Dropbox App Key, found on the dropbox developer console.</param>
        /// <param name="appSecret">Dropbox database connection, initialised after the user has obtained their access key.</param>
        public DropboxRelay(string appKey, string appSecret)
        {
            dropboxAppKey = appKey;
            dropboxAppSecret = appSecret;
        }

        /// <summary>
        /// Attempts to connect to a users dropbox. First, the config.json file will be checked for an access key which will be used to attempt to gain access to a users Dropbox. If this fails for whatever reason, then the user will go through Dropbox's OAuth2 procedure for obtaining an access key.
        /// </summary>
        public void ConnectToDropbox(ref JsonValue config)
        {
            bool connected = false;
            while (!connected)
            {
                //Attempt to load a previously saved access key from the config file
                string accessKey = (string)config["access_key"];

                //Attempt to start a Dropbox database connection with the access key
                try
                {
                    //If the loaded access key is not an empty string, try to establish a dropbox connection with it
                    if (accessKey != string.Empty)
                    {
                        //Establish the dropbox connection
                        Console.WriteLine("Attempting dropbox connection...");
                        dbx = new DropboxClient(accessKey);

                        //Check if the previously established connection is valid by making a small request of the users account name
                        var getAccount = Task.Run(dbx.Users.GetCurrentAccountAsync);
                        getAccount.Wait();
                        Console.WriteLine("Dropbox connection established. Connected as {0}!\n", getAccount.Result.Name.DisplayName);
                        connected = true;
                    }
                    else
                    {
                        Console.WriteLine("No access key found for user. Attempting to obtain one...\n");
                    }
                }
                catch
                {
                    Console.WriteLine("Access key from config JSON not valid. Will have to obtain another...\n");
                }

                try
                {
                    //Use Dropbox's OAuth2 feature to authenticate new users, if the user does not have a valid access key
                    if (!connected)
                    {
                        //Use the dropbox app key to redirect the user to a webpage, giving them the choice to allow this app to access their Dropbox
                        Console.WriteLine("Opening authorisation webpage...");
                        Uri webpage = DropboxOAuth2Helper.GetAuthorizeUri(dropboxAppKey);
                        System.Diagnostics.Process.Start(webpage.ToString());

                        //If the user chooses to allow the app to access their Dropbox, then they will be given a key to paste back into this app
                        Console.WriteLine("Please paste in the key provided to you below:");
                        string key = Console.ReadLine();
                        Console.WriteLine("\nThank you, attempting dropbox connection now...");

                        //Use this key in conjunction with the dropbox app secret to obtain an access key that can be used to access the users Dropbox
                        var getAccessKeyTask = Task.Run(() => DropboxOAuth2Helper.ProcessCodeFlowAsync(key, dropboxAppKey, dropboxAppSecret));
                        getAccessKeyTask.Wait();

                        //Save the new access key to the config JSON, format it to make it more human readable, and save it back to the app's root
                        config["access_key"] = getAccessKeyTask.Result.AccessToken.ToString();
                        File.WriteAllText("Config.JSON", config.ToString().Replace("{", "{" + Environment.NewLine).Replace(",", "," + Environment.NewLine));
                    }
                }
                catch
                {
                    Console.WriteLine("Something went wrong trying to obtain an access token. Program will now close...");
                    Console.ReadLine();
                    Environment.Exit(0);
                }
            }
        }

        /// <summary>
        /// Get the first file in the 'To Convert' folder in the users dropbox.
        /// </summary>
        /// <returns>The name of the first file in the 'To Convert' Folder if one is present, otherwise returns an empty string.</returns>
        public async Task<string> GetFileToConvert()
        {
            var list = await dbx.Files.ListFolderAsync("/To Convert");
            foreach (var item in list.Entries.Where(i => i.IsFile))
            {
                return "/To Convert/" + item.Name;
            }
            return string.Empty;
        }

        /// <summary>
        /// Checks if a file or folder exists in the users Dropbox folder.
        /// </summary>
        /// <param name="path">Path of the folder to check, from the root directory of the file converter folder.</param>
        /// <param name="name">Name of the file/folder to search for in the folder.</param>
        /// <param name="folder">Boolean flag, set to true if searching for a folder, or set to false if searching for a file.</param>
        /// <returns>Boolean flag, set to true if the specified file/folder is found, and set to false otherwise.</returns>
        public async Task<bool> DropboxExists(string path, string name, bool folder)
        {
            var list = await dbx.Files.ListFolderAsync(path);
            foreach (var item in list.Entries.Where(i => (folder ? i.IsFolder : i.IsFile)))
            {
                if (item.Name == name)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Checks if a file or folder exists, and deletes it if it does.
        /// </summary>
        /// <param name="path">Path of the folder to check, from the root directory of the file converter folder.</param>
        /// <param name="name">Name of the file/folder to search for in the folder.</param>
        /// <param name="folder">Boolean flag, set to true if searching for a folder, or set to false if searching for a file.</param>
        public async Task DeleteIfExists(string path, string name, bool folder)
        {
            var exists = await Task.Run(() => DropboxExists(path, name, folder));

            if (exists)
            {
                Console.WriteLine((folder ? "Folder" : "File") + ": " + name + " already exists in '" + path + "' Folder. " + (folder ? "Folder" : "File") + " will be overwritten");
                await dbx.Files.DeleteAsync(path + "/" + name);
            }
        }

        /// <summary>
        /// Checks if a given folder exists in the users Dropbox, and creates it if it does not.
        /// </summary>
        /// <param name="path">Path of the folder to check for.</param>
        /// <param name="name">Name of the folder to check for.</param>
        public async Task CreateIfNotPresent(string path, string name)
        {
            var exists = await Task.Run(() => DropboxExists(path, name, true));

            if (!exists)
                dbx.Files.CreateFolderAsync("/" + name).Wait();
        }

        /// <summary>
        /// Downloads a file from the users dropbox, given a path.
        /// </summary>
        /// <param name="path">The path of the file to be downloaded in the users dropbox.</param>
        /// <returns>A byte array of the data inside the file to be downloaded.</returns>
        public async Task<byte[]> Download(string path)
        {
            using (var response = await dbx.Files.DownloadAsync(path))
            {
                return await response.GetContentAsByteArrayAsync();
            }
        }

        /// <summary>
        /// Moves a file from a source path to a destination path within a users Dropbox.
        /// </summary>
        /// <param name="source">The location of the file being moved.</param>
        /// <param name="destination">The destination of the file being moved.</param>
        public void Movefile(string source, string destination)
        {
            dbx.Files.MoveAsync(source, destination).Wait();
        }

        /// <summary>
        /// Creates a folder at the specified path in the users Dropbox.
        /// </summary>
        /// <param name="path">The path for the folder to be created at.</param>
        public void CreateFolder(string path)
        {
            dbx.Files.CreateFolderAsync(path).Wait();
        }

        /// <summary>
        /// Uploads a file to the specified path in the users Dropbox.
        /// </summary>
        /// <param name="path">The path for the file to be uploaded to.</param>
        /// <param name="data">The data being uploaded.</param>
        public void UploadFile(string path, Stream data)
        {
            dbx.Files.UploadAsync(path, Dropbox.Api.Files.WriteMode.Overwrite.Instance, body: data).Wait();
        }

        /// <summary>
        /// Deletes a file at the specifed path from the users Dropbox.
        /// </summary>
        /// <param name="path">The path of the file to be deleted.</param>
        public void DeleteFile(string path)
        {
            dbx.Files.DeleteAsync(path).Wait();
        }
    }
}