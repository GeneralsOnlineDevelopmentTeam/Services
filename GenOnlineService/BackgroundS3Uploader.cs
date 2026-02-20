

using Amazon.S3;
using Amazon.S3.Model;
using GenOnlineService;
using MySqlX.XDevAPI.Common;
using Sentry.Protocol;
using System.Collections.Concurrent;
using System.Net;
using static Database.Functions.Lobby;

public enum ES3UploadType
{
    Screenshot = 0,
    Replay = 1
}

public enum ES3QueueUploadResult
{
    Success,
    Failed_InvalidUploadType,
    Failed_InvalidImage_Size,
    Failed_InvalidImage_Header
}

class S3QueuedUploadEntry
{
    public ES3UploadType m_uploadType { get; init; }
    public List<byte> m_FileData { get; init; }
    public int m_slotIndexInLobby { get; init; }
    public UInt64 m_MatchID { get; init; }
    public Int64 m_UserID { get; init; }
    public EScreenshotType m_screenshotTypeIfScreenshot { get; init; }

    public S3QueuedUploadEntry(ES3UploadType uploadType, byte[] fileBytes, UInt64 match_id, Int64 user_id, int slotIndexInLobby, EScreenshotType screenshotTypeIfScreenshot)
    {
        m_uploadType = uploadType;
        m_FileData = new List<byte>(fileBytes);
        m_MatchID = match_id;
        m_UserID = user_id;
        m_screenshotTypeIfScreenshot = screenshotTypeIfScreenshot;
        m_slotIndexInLobby = slotIndexInLobby;
    }
}

static class BackgroundS3Uploader
{
    private static ConcurrentQueue<S3QueuedUploadEntry> m_queueUploads = new();

    private static Int64 g_LastUpload = -1;

    private static Thread g_BackgroundThread = null;
    private static bool g_bShutdownRequested = false;

    public static void Initialize()
    {
        g_BackgroundThread = new Thread(new ThreadStart(TickThreaded));
        g_BackgroundThread.Start();
    }

    public static void Shutdown()
    {
        g_bShutdownRequested = true;
        g_BackgroundThread.Join();
    }

    public static void TickThreaded() // This is called on a thread, and uploads one file at a time, and max of 10 per second
    {
        while (!g_bShutdownRequested)
        {
            // do we have something to upload?
            if (!m_queueUploads.IsEmpty)
            {
                if (Environment.TickCount64 - g_LastUpload > 100) // max one file per 100ms
                {
                    // queue the next thing
                    if (m_queueUploads.TryDequeue(out S3QueuedUploadEntry entry))
                    {
                        try
                        {
                            DoUpload(entry).GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error in BackgroundS3Uploader: {ex.Message}");
                        }
						g_LastUpload = Environment.TickCount64;
					}
                }
            }
            else
            {
                // just sleep for a second
                System.Threading.Thread.Sleep(1000);
            }

            System.Threading.Thread.Sleep(1);
        }
    }

    private static async Task DoUpload(S3QueuedUploadEntry entry)
    {
        {
            DateTime utcNow = DateTime.UtcNow;
            int hour = utcNow.Hour;
            int minute = utcNow.Minute;

            string strPerMatchUserIDKey = Helpers.ComputeMD5Hash(String.Format("{0}_{1}", entry.m_MatchID, entry.m_UserID)); ;
            string strFileName = null;
            Database.Functions.Lobby.EMetadataFileType fileType = EMetadataFileType.UNKNOWN;


			if (entry.m_uploadType == ES3UploadType.Screenshot)
            {
                fileType = EMetadataFileType.FILE_TYPE_SCREENSHOT;

				string screenshotTypePrefix = "screenshot";
                if (entry.m_screenshotTypeIfScreenshot == EScreenshotType.SCREENSHOT_TYPE_LOADSCREEN)
                {
                    screenshotTypePrefix = "loadscreen";
                }
                else if (entry.m_screenshotTypeIfScreenshot == EScreenshotType.SCREENSHOT_TYPE_GAMEPLAY)
                {
                    screenshotTypePrefix = "gameplay";
                }
                else if (entry.m_screenshotTypeIfScreenshot == EScreenshotType.SCREENSHOT_TYPE_SCORESCREEN)
                {
                    screenshotTypePrefix = "scorescreen";
                }

                strFileName = String.Format("screenshot_{0}_{1}_{2}.jpg", screenshotTypePrefix, hour, minute);
            }
            else if (entry.m_uploadType == ES3UploadType.Replay)
            {
				fileType = EMetadataFileType.FILE_TYPE_REPLAY;

				strFileName = String.Format("match_{0}_user_{1}_replay.rep", entry.m_MatchID, strPerMatchUserIDKey);
            }

            if (strFileName == null)
            {
                return;
            }

			string objectKey = String.Format("match_{0}/user_{1}/{2}", entry.m_MatchID, strPerMatchUserIDKey, strFileName);

			// get config
			if (Program.g_Config == null)
            {
                return;
            }

            IConfiguration? matchdataSettings = Program.g_Config.GetSection("MatchData");

            if (matchdataSettings == null)
            {
                return;
            }

            // is upload enabled?
            bool bUploadMatchData = matchdataSettings.GetValue<bool>("upload_match_data");
            if (!bUploadMatchData)
            {
                return;
            }

            string? strS3AccessKey = matchdataSettings.GetValue<string>("s3_access_key");
            string? strS3SecretKey = matchdataSettings.GetValue<string>("s3_secret_key");
            string? strS3BucketName = matchdataSettings.GetValue<string>("s3_bucket_name");
            string? strS3Endpoint = matchdataSettings.GetValue<string>("s3_endpoint");

            // now upload
            try
            {
                // Create S3 client with R2 endpoint
                var config = new AmazonS3Config
                {
                    ServiceURL = strS3Endpoint,
                    ForcePathStyle = true, // Required for R2
                    Timeout = TimeSpan.FromSeconds(30),
                    MaxErrorRetry = 2
                };

                using var client = new AmazonS3Client(strS3AccessKey, strS3SecretKey, config);

                // Upload file
                using MemoryStream fileStream = new MemoryStream(entry.m_FileData.ToArray());
                var putRequest = new PutObjectRequest
                {
                    BucketName = strS3BucketName,
                    Key = objectKey,
                    InputStream = fileStream,
                    DisablePayloadSigning = true,
                    DisableDefaultChecksumValidation = true,
                };

                var response = await client.PutObjectAsync(putRequest);
                Console.WriteLine($"SCREENSHOT uploaded successfully. {entry.m_FileData.Count} bytes. HHTTP Status Code: {response.HttpStatusCode}");

                // store in DB
                await Database.Functions.Lobby.AttachMatchHistoryMetadata(GlobalDatabaseInstance.g_Database, entry.m_MatchID, entry.m_slotIndexInLobby, strFileName, fileType);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error uploading file: {ex.Message}");

                // reqeueue at the end to try again
                BackgroundS3Uploader.QueueUpload(entry.m_uploadType, entry.m_FileData.ToArray(), entry.m_MatchID, entry.m_UserID, entry.m_slotIndexInLobby, entry.m_screenshotTypeIfScreenshot);
                return;
            }
        }
    }

    // TODO: Limit file sizes when queueing, since they could flood memory
    public static ES3QueueUploadResult QueueUpload(ES3UploadType uploadType, byte[] fileBytes, UInt64 match_id, Int64 user_id, int slotIndexInLobby, EScreenshotType screenshotTypeIfScreenshot)
    {
        if (uploadType == ES3UploadType.Screenshot)
        {
            if (fileBytes.Length < 3) // should have 3 bytes at least, thats the header size
            {
                return ES3QueueUploadResult.Failed_InvalidImage_Size;
            }
            
            if (fileBytes[0] != 0xFF || fileBytes[1] != 0xD8 || fileBytes[2] != 0xFF)
            {
                // must be jpg
                return ES3QueueUploadResult.Failed_InvalidImage_Header;
            }
        }
        else if (uploadType == ES3UploadType.Replay)
        {
            if (fileBytes.Length < 6) // should have 6 bytes at least, thats the header size
            {
                return ES3QueueUploadResult.Failed_InvalidImage_Size;
            }

            if (System.Text.Encoding.UTF8.GetString(fileBytes, 0, 6) != "GENREP")
            {
                // must be jpg
                return ES3QueueUploadResult.Failed_InvalidImage_Header;
            }
        }
        else
        {
            return ES3QueueUploadResult.Failed_InvalidUploadType;
        }

        // queue it
        S3QueuedUploadEntry newUploadEntry = new S3QueuedUploadEntry(uploadType, fileBytes, match_id, user_id, slotIndexInLobby, screenshotTypeIfScreenshot); ;
        m_queueUploads.Enqueue(newUploadEntry);

        return ES3QueueUploadResult.Success;

        // Upload img
        /*
        
        */

    }
}