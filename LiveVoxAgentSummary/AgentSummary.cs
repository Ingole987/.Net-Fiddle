using CsvHelper;
using CsvHelper.Configuration.Attributes;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Renci.SshNet;
using System.Globalization;
using System.Text;

namespace LiveVoxAgentSummary
{
    public class AgentSummary
    {
        private static readonly IMemoryCache Cache = new MemoryCache(new MemoryCacheOptions());

        public static async Task<string> GetAgentSummary(IConfiguration config)
        {
            try
            {
                string? clientName = config.GetSection("clientName").Value;
                string? userName = config.GetSection("userName").Value;
                string? password = config.GetSection("password").Value;
                string? lvAccess = config.GetSection("lvAccess").Value;
                string? LivevoxAUTHENTICATE_URL = config.GetSection("LivevoxAUTHENTICATE_URL").Value;
                string? LiveVoxBaseURL = config.GetSection("LiveVoxBaseURL").Value;

                CredentialsSFTP credentials = new CredentialsSFTP();
                credentials.Host = config.GetSection("HostSFTP").Value;
                string? port = config.GetSection("PortSFTP").Value;
                credentials.Port = Convert.ToInt32(port);
                credentials.UserName = config.GetSection("userNameSFTP").Value;
                credentials.Password = config.GetSection("passwordSFTP").Value;

                string? startDate = config.GetSection("startDate").Value;
                string? endDate = config.GetSection("endDate").Value;


                DateTime startDateTime;
                DateTime endDateTime;

                if (string.IsNullOrEmpty(startDate) || string.IsNullOrEmpty(endDate))
                {
                    startDateTime = DateTime.Today;
                    endDateTime = DateTime.Today;
                }
                else
                {
                    startDateTime = DateTime.Parse(startDate);
                    endDateTime = DateTime.Parse(endDate);
                }

                List<DateTime> consecutiveDates = new List<DateTime>();

                DateTime currentDate = startDateTime;

                while (currentDate <= endDateTime)
                {
                    consecutiveDates.Add(currentDate);

                    currentDate = currentDate.AddDays(1);
                }

                Dictionary<string, string> csvResponses = new Dictionary<string, string>();

                string date = "";

                AccessTokenResponseVM access = await GetSessionIdAsync(clientName, userName, password, lvAccess, LivevoxAUTHENTICATE_URL);

                //foreach (var time in consecutiveDates)
                //{
                //    DateTime dt1 = new DateTime(1970, 1, 1);
                //    DateTime dt2 = time;
                //    string newDateTime = ((dt2 - dt1).TotalMilliseconds).ToString();

                //    var response = await PostAsync(lvAccess, access, LiveVoxBaseURL, newDateTime);

                //    var csvData = await response.Content.ReadAsStringAsync();

                //    date = (time.ToString("yyyy/MM/dd")).Replace("/", "");
                //    csvResponses[date] = csvData;
                //}

                List<Task> task1 = consecutiveDates.Select(async time =>
                {
                    DateTime dt1 = new DateTime(1970, 1, 1);
                    DateTime dt2 = time;
                    string newDateTime = ((dt2 - dt1).TotalMilliseconds).ToString();

                    var response = await PostAsync(lvAccess, access, LiveVoxBaseURL, newDateTime);
                    var csvData = await response.Content.ReadAsStringAsync();

                    date = (time.ToString("yyyy/MM/dd")).Replace("/", "");
                    csvResponses[date] = csvData;
                }).ToList();

                await Task.WhenAll(task1);


                //Parallel.ForEach(csvResponses, async kvp =>
                //{
                //    string date = kvp.Key;
                //    string csvData = kvp.Value;

                //    if (!string.IsNullOrEmpty(csvData) && !csvData.Trim().Equals("{}"))
                //    {
                //        string uniqueFileName = $"{clientName}-AgentSummary-{Guid.NewGuid()}.csv";
                //        await ConvertSummaryToCSV(csvData, uniqueFileName, credentials, date);
                //    }
                //});
                //foreach (var kvp in csvResponses)
                //{
                //    date = kvp.Key;
                //    string csvData = kvp.Value;

                //    if (!string.IsNullOrEmpty(csvData) && !csvData.Trim().Equals("{}"))
                //    {
                //        string uniqueFileName = $"{clientName}-AgentSummary-{Guid.NewGuid()}.csv";

                //        await ConvertSummaryToCSV(csvData, uniqueFileName, credentials, date);
                //    }
                //}

                List<Task> tasks2 = csvResponses
                .Where(kvp => !string.IsNullOrEmpty(kvp.Value) && !kvp.Value.Trim().Equals("{}"))
                .Select(async kvp =>
                {
                    string date = kvp.Key;
                    string csvData = kvp.Value;
                    string uniqueFileName = $"{clientName}-AgentSummary-{Guid.NewGuid()}.csv";

                    await ConvertSummaryToCSV(csvData, uniqueFileName, credentials, date);
                })
                .ToList();

                await Task.WhenAll(tasks2);

                return "success";
            }
            catch (Exception ex)
            {
                throw new Exception("An error occurred while getting the agent summary.", ex);
            }
        }


        public static async Task ConvertSummaryToCSV(string csvData, string uniqueFileName, CredentialsSFTP credentials, string date)
        {
            try
            {
                var summary = JsonConvert.DeserializeObject<Data>(csvData);

                string currentDirectory = Directory.GetCurrentDirectory();
                string folder = Path.Combine(currentDirectory, "Content");
                Directory.CreateDirectory(folder);

                string csvFilePath = Path.Combine(folder, uniqueFileName);


                using (var writer = new StreamWriter(csvFilePath))
                using (var csv = new CsvWriter(writer, CultureInfo.InvariantCulture))
                {
                    csv.WriteHeader<Summary>();

                    csv.NextRecord();
                    foreach (var result in summary.results)
                    {
                        csv.WriteRecord(result);
                        csv.NextRecord();
                    }
                    writer.Flush();
                }

                string remoteDirectoryPath = $"/{date}";
                await UploadFileToSftp(csvFilePath, remoteDirectoryPath, credentials);
                System.IO.File.Delete(csvFilePath);
            }
            catch (Exception)
            {

                throw;
            }

        }

        public static async Task UploadFileToSftp(string localFilePath, string remoteDirectory, CredentialsSFTP credentials)
        {
            using (var client = new SftpClient(new PasswordConnectionInfo(credentials.Host, credentials.UserName, credentials.Password)))
            {
                try
                {
                    await Task.Run(() => client.Connect());
                    if (!client.Exists(remoteDirectory))
                    {
                        client.CreateDirectory(remoteDirectory);
                    }
                    else
                    {
                        var files = client.ListDirectory(remoteDirectory);
                        foreach (var file in files)
                        {
                            if (!file.IsDirectory)
                            {
                                client.DeleteFile(file.FullName);
                            }
                        }
                    }
                    if (File.Exists(localFilePath))
                    {
                        using (var fileStream = new FileStream(localFilePath, FileMode.Open))
                        {
                            string remoteFilePath = remoteDirectory + "/" + Path.GetFileName(localFilePath);
                            await Task.Run(() => client.UploadFile(fileStream, remoteFilePath));
                            Console.WriteLine(remoteDirectory + "-->"+Path.GetFileName(localFilePath));
                        }
                    }
                }
                catch (Renci.SshNet.Common.SshConnectionException ex)
                {
                    throw new Exception("An SSH connection error occurred.", ex);
                }
                catch (Exception ex)
                {
                    throw new Exception("An error occurred during the SFTP file upload.", ex);
                }
                finally
                {
                    client.Disconnect();
                }
            }
        }


        public static async Task<HttpResponseMessage> PostAsync(string lvAccess, AccessTokenResponseVM access, string LiveVoxBaseURL, string newDateTime)
        {
            using (var httpClient = new HttpClient())
            {
                try
                {
                    if (!string.IsNullOrEmpty(lvAccess))
                    {
                        httpClient.DefaultRequestHeaders.Add("LV-Access", Convert.ToString(lvAccess));
                    }
                    if (access != null && !string.IsNullOrEmpty(access.sessionId))
                    {
                        httpClient.DefaultRequestHeaders.Add("LV-Session", Convert.ToString(access.sessionId));
                    }

                    var jsonObj = new
                    {
                        startDate = newDateTime,
                        endDate = newDateTime
                    };

                    var json = Newtonsoft.Json.JsonConvert.SerializeObject(jsonObj);

                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    var response = await httpClient.PostAsync(LiveVoxBaseURL, content);

                    response.EnsureSuccessStatusCode();
                   
                    return response;
                }
                catch (HttpRequestException ex)
                {
                    throw new Exception("An error occurred while making the HTTP request.", ex);
                }
                catch (Exception ex)
                {
                    throw new Exception("An error occurred during the POST request.", ex);
                }
            }
        }

        public static async Task<AccessTokenResponseVM> GetSessionIdAsync(string clientName, string userName, string password, string lvAccess, string LivevoxAUTHENTICATE_URL)
        {
            var cacheKey = $"{clientName}-{userName}-{password}";

            if (Cache.TryGetValue(cacheKey, out AccessTokenResponseVM cachedSession))
            {
                return cachedSession;
            }

            try
            {
                using (var httpClient = new HttpClient())
                {
                    var requestParameter = new AccessTokenRequestVM
                    {
                        clientName = clientName,
                        userName = userName,
                        password = password
                    };

                    httpClient.DefaultRequestHeaders.Add("LV-Access", Convert.ToString(lvAccess));

                    var json = JsonConvert.SerializeObject(requestParameter);

                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    var response = await httpClient.PostAsync(LivevoxAUTHENTICATE_URL, content);

                    if (response != null && response.IsSuccessStatusCode)
                    {
                        var session = JsonConvert.DeserializeObject<AccessTokenResponseVM>(response.Content.ReadAsStringAsync().Result);
                        Cache.Set(cacheKey, session, TimeSpan.FromMinutes(60));
                        return session;
                    }
                    else
                    {
                        throw new Exception(response.StatusCode.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"{ex.Message}");
            }
        }
    }


    public class AccessTokenResponseVM
    {
        public string sessionId { get; set; }
        public int? userId { get; set; }
        public int? clientId { get; set; }
        public int? daysUntilPasswordExpires { get; set; }

        public int? code { get; set; }
        public string message { get; set; }
    }
    public class AccessTokenRequestVM
    {
        public string clientName { get; set; }
        public string userName { get; set; }
        public string password { get; set; }
    }
    public class Data
    {
        public List<Summary> results { get; set; }
    }
    public class Summary
    {
        [Name("loginId")]
        public string loginId { get; set; }

        [Name("firstName")]
        public string firstName { get; set; }

        [Name("lastName")]
        public string lastName { get; set; }

        public Details details { get; set; }
    }
    public class Details
    {
        public int successfulOpTransfer { get; set; }
        public int successfulTransactionalEmail { get; set; }
        public int successfulTransactionalSms { get; set; }
        public double inCallTimeInMinutes { get; set; }
        public double inCallTimeInPercent { get; set; }
        public double readyTimeInMinutes { get; set; }
        public double readyTimeInPercent { get; set; }
        public double wrapupTimeInMinutes { get; set; }
        public double wrapupTimeInPercent { get; set; }
        public double notReadyTimeInMinutes { get; set; }
        public double notReadyTimeInPercent { get; set; }
        public int rpcPaymentPtp { get; set; }
        public int rpcNoPaymentPtp { get; set; }
        public int wpc { get; set; }
        public int nonConnects { get; set; }
        public int totalRpcs { get; set; }
    }
    public class CredentialsSFTP
    {
        public string? UserName { get; set; }
        public string? Password { get; set; }
        public string? Host { get; set; }
        public int Port { get; set; }
    }

}