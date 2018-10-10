using System;
using System.Linq;
using System.Threading.Tasks;
using System.Json;
using System.Net;
using System.Net.Http;
using System.IO;

namespace Dropbox_File_Converter
{
    class Program
    {
        /// <summary>
        /// DropboxRelay instance, which will be responsible for all communication between the client and Dropbox's servers
        /// </summary>
        static DropboxRelay relay;
        //Visit https://www.dropbox.com/developers/apps or read the README for more information on obtaining your app key and secret
        /// <summary>
        /// Json Value that will contain data from the local config file.
        /// </summary>
        static JsonValue config;

        static void Main(string[] args)
        {
            Console.WriteLine("Initialising...\n");

            //Initialise the program by getting the config file from the apps root
            GetConfig();

            //Establish a connection to the users dropbox
            relay.ConnectToDropbox(ref config);

            //Ensure that the correct folders are present in the users Dropbox folder
            Task.Run(() => relay.CreateIfNotPresent(string.Empty, "Can't Convert")).Wait();
            Task.Run(() => relay.CreateIfNotPresent(string.Empty, "Converted")).Wait();
            Task.Run(() => relay.CreateIfNotPresent(string.Empty, "To Convert")).Wait();

            //Start an endless loop, which will continuously check for files in the 'To Convert' Dropbox folder and try to convert them
            while (true)
            {
                //Check if a file is in the 'toConvert' folder in the users dropbox, getting the files name and extension if it exists
                Console.WriteLine("Searching for files...");
                Task<string> getFileTask = Task.Run((Func<Task<string>>) relay.GetFileToConvert);
                getFileTask.Wait();
                string fullPath = getFileTask.Result;
                string[] splitPath = fullPath.Split('.');

                //If a file was found, convert it and upload it to the converted folder
                if (fullPath != string.Empty)
                {
                    //Special case - tar.bz2 and tar.gz extensions
                    if (splitPath.Length == 3)
                    {
                        splitPath = new string[] { splitPath[0], "tar." + splitPath[2] };
                    }

                    //Assign the elements of splitPath different variables to make them more easier to understand
                    string path = splitPath[0];
                    string extension = splitPath[1];
                    string fileName = splitPath[0].Split('/').Last();

                    //Inform the user that a file has been found
                    Console.WriteLine("'" + extension + "'" + " File Found at:  " + path);
                    bool converted = false;

                    //Check if the user defined config JSON contains a target format for the file to be converted
                    if(config["conversions"].ContainsKey(extension))
                    {
                        //Check if the source and target formats in the config JSON are the same, which would make a conversion unnecessary
                        if ((string)config["conversions"][extension] == extension)
                        {
                            Console.WriteLine("Target format is same as source format in config JSON, no conversion necessary.");
                        }
                        else
                        {
                            //Download the file
                            var downloadTask = Task.Run(() => relay.Download(fullPath));
                            downloadTask.Wait();
                            var data = downloadTask.Result;

                            //Attempt to convert the file
                            Console.WriteLine("Converting file to format: " + config["conversions"][extension]);
                            converted = ConvertAndUploadFile(fullPath, data, fileName, extension, (string)config["conversions"][extension]);
                        }
                    }
                    else
                    {
                        Console.WriteLine("No conversion data found for extension: " + extension);
                    }

                    //If the file cannot be converted, put it into the 'Can't Convert' folder in dropbox
                    if(!converted)
                    {
                        Console.WriteLine("Could not convert file. File has been moved to the 'Can't Convert' Folder.");
                        Task.Run(() => relay.DeleteIfExists("/Can't Convert", fileName + "." + extension, false)).Wait();
                        relay.Movefile(fullPath, "/Can't Convert/" + fileName + "." + extension);
                    }
                    else
                    {
                        Console.WriteLine("File has been converted successfully and placed in the 'Converted' folder.");
                    }
                }
                else
                {
                    //Inform the user that no files to convert were found
                    Console.WriteLine("No files to convert found.");
                }

                //Sleep for 3 seconds before clearing the console and checking for more files
                System.Threading.Thread.Sleep(3000);
                Console.Clear();
            }
        }

        /// <summary>
        /// Uploads a file to zamzar's servers for conversion, given a target format, then gets each resultant file and uploads to the users dropbox
        /// </summary>
        /// <param name="path">Path of the file to be converted in the users dropbox.</param>
        /// <param name="data">Data contained in the file to be converted.</param>
        /// <param name="name">Name of the file to be converted.</param>
        /// <param name="from">Source extension of the file to be converted.</param>
        /// <param name="to">Target extension of the file to be converted</param>
        /// <returns>A boolean value which states whether the conversion and upload was successful or not.</returns>
        static bool ConvertAndUploadFile(string path, byte[] data, string name, string from, string to)
        {
            const string endpoint = "https://sandbox.zamzar.com/v1/";

            //Check if it is possible to convert from requested source format to requested target format using Zamzar by querying Zamzar's servers
            JsonValue json = GetPossibleConversions(endpoint + "formats/" + from).Result["targets"];
            bool found = false;

            for (int i = 0; i < json.Count; i++)
                if ((string)json[i]["name"] == to)
                    found = true;

            //If the conversion from 'from' to 'to' is not possible using Zamzar, then return false, signalling the failure of the conversion
            if (!found)
            {
                Console.WriteLine("Cannot Convert File: " + "'" + to + "'" + " Target format not found");
                return false;
            }

            //Upload the file to Zamzar's servers and obtain the job id for it
            int jobId = (int)UploadToConvert(endpoint + "jobs", name + "." + from, data, to).Result["id"];
            int fileId;
            Console.WriteLine("File Uploaded!\nJob ID: " + jobId.ToString());

            //Keep checking if the uploaded file has been converted
            while (true)
            {
                json = CheckIfFinished(endpoint + "jobs/" + jobId.ToString()).Result;
                Console.WriteLine("Conversion Status: " + (string)json["status"]);

                //When the uploaded file has been converted, download each resultant file and upload it to dropbox
                if ((string)json["status"] == "successful")
                {
                    JsonArray items = (JsonArray)json["target_files"];
                    Console.WriteLine("Converted into " + items.Count.ToString() + (items.Count == 1 ? " file." : " files."));

                    //If there are more than 1 resultant files, then create a folder for them all in the Converted folder, overwriting any previously existing folders with the same name
                    if (items.Count > 1)
                    {
                        Task.Run(() => relay.DeleteIfExists("/Converted", name, true)).Wait();
                        relay.CreateFolder("/Converted/" + name);
                    }

                    //Get each resultant file and upload it to the users 'Converted' Folder
                    for (int i = 0; i < items.Count; i++)
                    {
                        Console.WriteLine("Getting file: " + (i + 1).ToString());
                        fileId = (int)items[i]["id"];
                        var downloadConvertedFileTask = Task.Run(() => DownloadConvertedFile(endpoint + "files/" + fileId.ToString() + "/content"));
                        downloadConvertedFileTask.Wait();
                        relay.UploadFile("/Converted/" + name + (items.Count == 1 ? "" : "/" + name + i.ToString()) + "." + to, downloadConvertedFileTask.Result);
                        Console.WriteLine("Uploaded file " + (i + 1).ToString() + " to Dropbox.");
                    }
                    break;
                }
                else if ((string)json["status"] == "failed")
                {
                    Console.WriteLine("Conversion failed for unknown reason.");
                    return false;
                }
            }

            //Delete the original file and return true, signalling a successful conversion and upload
            relay.DeleteFile(path);
            return true;
        }

        /// <summary>
        /// Gets config data from the user defined config file and loads it into the program.
        /// </summary>
        static void GetConfig()
        {
            Console.WriteLine("Fetching config file...");

            try
            {
                config = JsonValue.Parse(File.ReadAllText("dropbox_file_converter_config.json"));
                Console.WriteLine("Config file fetched!\n");
            }
            catch (FileNotFoundException)
            {
                Console.WriteLine("Config file (dropbox_file_converter_config.json) not found! Program will now close.");
                Console.ReadLine();
                Environment.Exit(0);
            }
            catch (FormatException)
            {
                Console.WriteLine("The config file is not valid JSON. It is either corrupt or has been improperly edited. Program will now close");
                Console.ReadLine();
                Environment.Exit(0);
            }

            // validate config
            if (!config.ContainsKey("access_key")
                    || !config.ContainsKey("zamzar_api_key")
                    || !config.ContainsKey("dropbox_api_key")
                    || !config.ContainsKey("dropbox_api_secret")) {
                Console.WriteLine("The config file didn't have one or more of the required keys:\n"
                        + "    \"access_key\"\n"
                        + "    \"zamzar_api_key\"\n"
                        + "    \"dropbox_api_key\"\n"
                        + "    \"dropbox_api_secret\"\n"
                        + "Program will now close");
                Console.ReadLine();
                Environment.Exit(0);
            }

            relay = new DropboxRelay((string) config["dropbox_api_key"], (string) config["dropbox_api_secret"]);
        }

        /// <summary>
        /// Gets a list of all the possible conversion types for a provided extension from the Zamzar servers.
        /// </summary>
        /// <param name="url">Endpoint for the query, containing the source format</param>
        /// <returns>A JsonValue containing a list of all possible formats that the passed in format can be converted to.</returns>
        static async Task<JsonValue> GetPossibleConversions(string url)
        {
            using (HttpClientHandler handler = new HttpClientHandler { Credentials = new NetworkCredential((string) config["zamzar_api_key"], "") })
            using (HttpClient client = new HttpClient(handler))
            using (HttpResponseMessage response = await client.GetAsync(url))
            using (HttpContent content = response.Content)
            {
                string data = await content.ReadAsStringAsync();
                return JsonValue.Parse(data);
            }
        }

        /// <summary>
        /// Upload a file to Zamzar's servers to be converted into a specified target format.
        /// </summary>
        /// <param name="url">Endpoint for the convert operation, containing the source format.</param>
        /// <param name="name">Name of the file being converted.</param>
        /// <param name="sourceData">Data of the file being converted.</param>
        /// <param name="targetFormat">Target format for the file to be converted to.</param>
        /// <returns></returns>
        static async Task<JsonValue> UploadToConvert(string url, string name, byte[] sourceData, string targetFormat)
        {
            using (HttpClientHandler handler = new HttpClientHandler { Credentials = new NetworkCredential((string) config["zamzar_api_key"], "") })
            using (HttpClient client = new HttpClient(handler))
            {
                var request = new MultipartFormDataContent();
                request.Add(new StringContent(targetFormat), "target_format");
                request.Add(new StreamContent(new MemoryStream(sourceData)), "source_file", new FileInfo(name).Name);
                using (HttpResponseMessage response = await client.PostAsync(url, request))
                using (HttpContent content = response.Content)
                {
                    string data = await content.ReadAsStringAsync();
                    return JsonValue.Parse(data);
                }
            }
        }

        /// <summary>
        /// Checks if a conversion job has been successful or not.
        /// </summary>
        /// <param name="url">Endpoint for the check query, containing the Job ID of the conversion job</param>
        /// <returns>Returned JSON value, containing the status of the conversion job, and the file ID's of any converted files.</returns>
        static async Task<JsonValue> CheckIfFinished(string url)
        {
            using (HttpClientHandler handler = new HttpClientHandler { Credentials = new NetworkCredential((string) config["zamzar_api_key"], "") })
            using (HttpClient client = new HttpClient(handler))
            using (HttpResponseMessage response = await client.GetAsync(url))
            using (HttpContent content = response.Content)
            {
                string data = await content.ReadAsStringAsync();
                return JsonValue.Parse(data);
            }
        }

        /// <summary>
        /// Downloads a file that has already been converted from Zamzar's servers.
        /// </summary>
        /// <param name="url">Endpoint for the download request, containing the File ID of the file to be downloaded.</param>
        /// <returns>A stream containing the data of the downloaded object.</returns>
        static async Task<Stream> DownloadConvertedFile(string url)
        {
            //Can't use 'using' keyword in this method, as the returned stream was being closed automatically when the HttpContent was disposed - will be garbage collected instead
            HttpClientHandler handler = new HttpClientHandler { Credentials = new NetworkCredential((string) config["zamzar_api_key"], "") };
            HttpClient client = new HttpClient(handler);
            HttpResponseMessage response = await client.GetAsync(url);
            HttpContent content = response.Content;
            {
                Stream stream = await content.ReadAsStreamAsync();
                return stream;
            }
        }
    }
}
