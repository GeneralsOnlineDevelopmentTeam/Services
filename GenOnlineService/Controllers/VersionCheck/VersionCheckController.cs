/*
**    GeneralsOnline Game Services - Backend Services for Command & Conquer Generals Online: Zero Hour
**    Copyright (C) 2025  GeneralsOnline Development Team
**
**    This program is free software: you can redistribute it and/or modify
**    it under the terms of the GNU Affero General Public License as
**    published by the Free Software Foundation, either version 3 of the
**    License, or (at your option) any later version.
**
**    This program is distributed in the hope that it will be useful,
**    but WITHOUT ANY WARRANTY; without even the implied warranty of
**    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
**    GNU Affero General Public License for more details.
**
**    You should have received a copy of the GNU Affero General Public License
**    along with this program.  If not, see <https://www.gnu.org/licenses/>.
*/

using Microsoft.AspNetCore.Mvc;
using System;
using System.Text;
using System.Text.Json;

namespace GenOnlineService.Controllers
{
	public enum EVersionCheckResult
	{
		OK = 0,
		Failed = 1,
		NeedsUpdate = 2
	};

	public abstract class APIResult
	{
		public string Serialize()
		{
			string strRetVal = JsonSerializer.Serialize(Convert.ChangeType(this, GetReturnType()));
			return strRetVal;
		}

		public abstract Type GetReturnType();
	}

	public class POST_VersionCheck_Result : APIResult
	{
		public override Type GetReturnType()
		{
			return typeof(POST_VersionCheck_Result);
		}

		public EVersionCheckResult result { get; set; } = EVersionCheckResult.Failed;
		public string patcher_name { get; set; } = String.Empty;
		public string patcher_path { get; set; } = String.Empty;
		public Int64 patcher_size { get; set; } = 0;
	}

	// legacy version checker
	[ApiController]
	[Route("/cloud/env:prod/VersionCheck")]
	public class VersionCheckLegacyController : ControllerBase
	{
		private readonly ILogger<VersionCheckLegacyController> _logger;

		public VersionCheckLegacyController(ILogger<VersionCheckLegacyController> logger)
		{
			_logger = logger;
		}

		[HttpPost(Name = "PostVersionCheckLegacy")]
		public async Task<APIResult> Post()
		{
			using (var reader = new StreamReader(HttpContext.Request.Body))
			{
				string jsonData = await reader.ReadToEndAsync();

#if !DEBUG
				return VersionHelper.Post_InternalHandler(jsonData);
#else
				return await VersionHelper.Post_InternalHandler(jsonData);
#endif
			}
		}
	}

	[ApiController]
	[Route("env/{environment}/contract/{contract_version}/[controller]")]
	public class VersionCheckController : ControllerBase
	{

		public VersionCheckController()
		{

		}

		[HttpPost(Name = "PostVersionCheck")]
		public async Task<APIResult> Post()
		{		
			using (var reader = new StreamReader(HttpContext.Request.Body))
			{
				string jsonData = await reader.ReadToEndAsync();

#if !DEBUG
				return VersionHelper.Post_InternalHandler(jsonData);
#else
				return await VersionHelper.Post_InternalHandler(jsonData);
#endif
			}
		}
	}

	class VersionHelper
	{
		private static readonly HttpClient s_httpClient = new HttpClient 
		{ 
			Timeout = TimeSpan.FromSeconds(10) 
		};

#if !DEBUG
		public static APIResult Post_InternalHandler(string jsonData)
#else
		public static async Task<APIResult> Post_InternalHandler(string jsonData)
#endif
		{
			POST_VersionCheck_Result result = new POST_VersionCheck_Result();

			var options = new JsonSerializerOptions
			{
				PropertyNameCaseInsensitive = true
			};

			try
			{
				var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(jsonData, options);

				if (data != null && data.ContainsKey("ver") && data.ContainsKey("netver") && data.ContainsKey("servicesver"))
				{
					if (UInt32.TryParse(data["execrc"].ToString(), out UInt32 execrc)
						&& Int32.TryParse(data["ver"].ToString(), out int ver)
						&& Int32.TryParse(data["netver"].ToString(), out int netver)
						&& Int32.TryParse(data["servicesver"].ToString(), out int servicesver))
					{
						// TODO: Check this exists on startup for safety
						// TODO: maybe cache it?
#if DEBUG
						UInt32 calculatedCRC_Exe_30 = 0;
						UInt32 calculatedCRC_Exe_60 = 0;
#else
						UInt32 calculatedCRC_Exe_30 = CRC32Calculator.CalculateCRC32(Path.Combine(Directory.GetCurrentDirectory(), "crcfiles", "GeneralsOnlineZH_30.exe"));
						UInt32 calculatedCRC_Exe_60 = CRC32Calculator.CalculateCRC32(Path.Combine(Directory.GetCurrentDirectory(), "crcfiles", "GeneralsOnlineZH_60.exe"));
#endif

#if DEBUG
#if LARGE_PATCH_TEST
						const bool bDoCRCChecks = true;
#else
						const bool bDoCRCChecks = false;
#endif
#else
						const bool bDoCRCChecks = true;
#endif

						// TODO: Service should have pulses on lobby from members and remove if they dont do within X seconds

						bool bVersionMatches = ver == Constants.GENERALS_ONLINE_VERSION;
						bool bNetVersionMatches = netver == Constants.GENERALS_ONLINE_NET_VERSION;
						bool bServicesVersionMatches = servicesver == Constants.GENERALS_ONLINE_SERVICE_VERSION;
						bool bExeCRCMatches = execrc == calculatedCRC_Exe_30 || execrc == calculatedCRC_Exe_60;

						if (!bDoCRCChecks || (bVersionMatches && bNetVersionMatches && bServicesVersionMatches && bExeCRCMatches))
						{
							result.result = EVersionCheckResult.OK;
						}
						else
						{
							try
							{
								var jsonPatchData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(System.IO.File.ReadAllText(Path.Combine("data", "patchdata.json")), options);

								if (jsonPatchData != null && jsonPatchData.ContainsKey("patcher_name") && jsonPatchData.ContainsKey("patcher_path"))
								{
									result.patcher_name = jsonPatchData["patcher_name"].ToString();
									result.patcher_path = jsonPatchData["patcher_path"].ToString();

#if !DEBUG
								string strPatcherPath = Path.Combine(Directory.GetCurrentDirectory(), "crcfiles", result.patcher_name);
								//string strPatchPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "public_html", "updater", "v1.gopatch");

								if (File.Exists(strPatcherPath))
								{
									result.patcher_size = (UInt32)new FileInfo(strPatcherPath).Length;
								}
								else
								{
									result.result = EVersionCheckResult.Failed;
								}
#else
							// large patch test
#if LARGE_PATCH_TEST
							result.patcher_path = "http://ipv4.download.thinkbroadband.com/100MB.zip";

							result.patcher_size = 100 * 1048576;
#else

							// TODO: Client should probably just make HEAD requests later
							// debug hack... since we dont have local data

							async Task<long> GetHTTPSize(string url)
							{
								try
								{
									HttpRequestMessage request = new HttpRequestMessage(System.Net.Http.HttpMethod.Head, url);
									HttpResponseMessage response = await s_httpClient.SendAsync(request);

									if (response.IsSuccessStatusCode && response.Content.Headers.ContentLength.HasValue)
									{
										return response.Content.Headers.ContentLength.Value;
									}
								}
								catch (HttpRequestException)
								{
									// TODO: Log exception or handle error
								}

								return -1; // Return -1 if the size could not be determined
							}

							result.patcher_size = await GetHTTPSize(result.patcher_path);
#endif

#endif

								result.result = EVersionCheckResult.NeedsUpdate;
								}
								else
								{
									result.result = EVersionCheckResult.Failed;
								}
							}
							catch (Exception ex)
							{
								Console.WriteLine($"Error loading patchdata.json: {ex.Message}");
								result.result = EVersionCheckResult.Failed;
							}
						}
					}
					else
					{
						result.result = EVersionCheckResult.Failed;
					}
				}
				else
				{
					return result;
				}
			}
			catch
			{
				return result;
			}

			return result;
		}
	}
}
