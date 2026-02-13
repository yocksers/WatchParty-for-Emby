using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WatchPartyForEmby
{
    public class ExternalWebServer
    {
        private readonly ILogger _logger;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly HttpListener _listener;
        private CancellationTokenSource _cancellationTokenSource;
        private readonly int _port;

        public ExternalWebServer(ILogger logger, IJsonSerializer jsonSerializer, int port)
        {
            _logger = logger;
            _jsonSerializer = jsonSerializer;
            _port = port;
            _cancellationTokenSource = new CancellationTokenSource();

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://*:{port}/");
        }

        public string Start()
        {
            try
            {
                _listener.Start();
                var prefix = _listener.Prefixes.First();
                _logger.Info($"[ExternalWebServer] Started and listening on {prefix}");
                Task.Run(() => Listen(_cancellationTokenSource.Token));
                return $"Running on port {_port}";
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5)
            {
                var errorMsg = $"Error: Access Denied. On Windows, please run this command as an Administrator: netsh http add urlacl url=http://*:{_port}/ user=\"Everyone\". On Linux/Docker, ensure the port is available and Emby has network permissions.";
                _logger.Error(errorMsg);
                return errorMsg;
            }
            catch (Exception ex)
            {
                var errorMsg = $"[ExternalWebServer] Failed to start: {ex.Message}";
                _logger.ErrorException(errorMsg, ex);
                return errorMsg;
            }
        }

        public void Stop()
        {
            if (!_listener.IsListening) return;
            _cancellationTokenSource.Cancel();
            _listener.Stop();
            _listener.Close();
            _logger.Info("[ExternalWebServer] Stopped.");
        }

        private async Task Listen(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    await ProcessRequest(context);
                }
                catch (ObjectDisposedException) { break; }
                catch (HttpListenerException) { break; }
                catch (Exception ex)
                {
                    _logger.ErrorException("[ExternalWebServer] Error processing request.", ex);
                }
            }
        }

        private async Task ProcessRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            if (request.Url == null)
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                response.OutputStream.Close();
                return;
            }

            try
            {
                response.AddHeader("Access-Control-Allow-Origin", "*");
                response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.AddHeader("Access-Control-Allow-Headers", "Content-Type");

                if (request.HttpMethod == "OPTIONS")
                {
                    response.StatusCode = (int)HttpStatusCode.OK;
                    response.OutputStream.Close();
                    return;
                }

                if (request.Url.AbsolutePath.StartsWith("/api/"))
                {
                    if (request.HttpMethod == "POST")
                    {
                        string requestBody;
                        using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                        {
                            requestBody = await reader.ReadToEndAsync();
                        }
                        await HandleApiRequest(request.Url.AbsolutePath, request.QueryString, response, requestBody);
                    }
                    else
                    {
                        await HandleApiRequest(request.Url.AbsolutePath, request.QueryString, response, null);
                    }
                    return;
                }

                await HandleFileRequest(request.Url.AbsolutePath, response);
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[ExternalWebServer] Error during request to {request.Url}:", ex);
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await WriteResponse(response, "An internal server error occurred.");
            }
            finally
            {
                response.OutputStream.Close();
            }
        }

        private async Task HandleFileRequest(string path, HttpListenerResponse response)
        {
            var resourcePath = (path == "/" || path == "/index.html") ? "external.html" : path.TrimStart('/');
            var resourceName = $"{GetType().Namespace}.Configuration.{resourcePath}";
            var assembly = GetType().GetTypeInfo().Assembly;

            _logger.Debug($"[ExternalWebServer] Attempting to serve: {resourceName}");

            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    _logger.Warn($"[ExternalWebServer] Resource not found: {resourceName}");
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    await WriteResponse(response, "404 Not Found");
                    return;
                }

                response.StatusCode = (int)HttpStatusCode.OK;
                response.ContentType = GetContentType(resourcePath);
                await stream.CopyToAsync(response.OutputStream);
            }
        }

        private async Task HandleApiRequest(string path, System.Collections.Specialized.NameValueCollection queryString, HttpListenerResponse response, string requestBody)
        {
            if (path.StartsWith("/api/emby/"))
            {
                var config = Plugin.Instance.Configuration;
                var apiKey = config.EmbyApiKey;
                
                if (string.IsNullOrEmpty(apiKey))
                {
                    response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                    await WriteResponse(response, "{\"error\":\"Emby API key not configured in plugin settings\"}");
                    return;
                }
                
                var embyPath = path.Substring("/api/emby".Length);
                using (var client = new System.Net.Http.HttpClient())
                {
                    var embyUrl = $"http://localhost:8096{embyPath}";
                    var queryParams = new List<string>();
                    
                    if (queryString.Count > 0)
                    {
                        foreach (var key in queryString.AllKeys)
                        {
                            queryParams.Add($"{key}={Uri.EscapeDataString(queryString[key])}");
                        }
                    }
                    
                    queryParams.Add($"api_key={Uri.EscapeDataString(apiKey)}");
                    embyUrl += "?" + string.Join("&", queryParams);
                    
                    try
                    {
                        var embyResponse = await client.GetAsync(embyUrl);
                        var content = await embyResponse.Content.ReadAsStringAsync();
                        
                        response.StatusCode = (int)embyResponse.StatusCode;
                        response.ContentType = "application/json";
                        await WriteResponse(response, content);
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorException("[ExternalWebServer] Error proxying to Emby", ex);
                        response.StatusCode = (int)HttpStatusCode.InternalServerError;
                        await WriteResponse(response, "{\"error\":\"Failed to proxy request\"}");
                    }
                }
            }
            else if (path == "/api/config/strm-library" && requestBody == null)
            {
                var config = Plugin.Instance.Configuration;
                var result = new
                {
                    targetLibraryId = config.StrmTargetLibraryId,
                    targetLibraryName = config.StrmTargetLibraryName
                };
                
                var json = _jsonSerializer.SerializeToString(result);
                response.StatusCode = (int)HttpStatusCode.OK;
                response.ContentType = "application/json";
                await WriteResponse(response, json);
            }
            else if (path == "/api/config/external-url" && requestBody == null)
            {
                var config = Plugin.Instance.Configuration;
                var result = new
                {
                    externalServerUrl = config.ExternalServerUrl ?? string.Empty
                };
                
                var json = _jsonSerializer.SerializeToString(result);
                response.StatusCode = (int)HttpStatusCode.OK;
                response.ContentType = "application/json";
                await WriteResponse(response, json);
            }
            else if (path == "/api/users/lookup" && requestBody != null)
            {
                try
                {
                    var request = _jsonSerializer.DeserializeFromString<Dictionary<string, object>>(requestBody);
                    var userId = request.ContainsKey("userId") ? request["userId"]?.ToString() : null;
                    
                    if (string.IsNullOrEmpty(userId))
                    {
                        response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteResponse(response, "{\"error\":\"userId is required\"}");
                        return;
                    }
                    
                    var embyServerUrl = Plugin.Instance.Configuration.ExternalServerUrl;
                    if (string.IsNullOrEmpty(embyServerUrl))
                    {
                        embyServerUrl = "http://localhost:8096";
                    }
                    
                    var apiKey = Plugin.Instance.Configuration.EmbyApiKey;
                    var userUrl = $"{embyServerUrl}/emby/Users/{userId}?api_key={apiKey}";
                    
                    using (var httpClient = new System.Net.Http.HttpClient())
                    {
                        var userJson = await httpClient.GetStringAsync(userUrl);
                        var userData = _jsonSerializer.DeserializeFromString<Dictionary<string, object>>(userJson);
                        var userName = userData.ContainsKey("Name") ? userData["Name"]?.ToString() : "Unknown";
                        
                        var result = new { userId = userId, userName = userName };
                        var resultJson = _jsonSerializer.SerializeToString(result);
                        response.StatusCode = (int)HttpStatusCode.OK;
                        response.ContentType = "application/json";
                        await WriteResponse(response, resultJson);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error($"[ExternalWebServer] Error looking up user: {ex.Message}");
                    response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    await WriteResponse(response, $"{{\"error\":\"{ex.Message}\"}}");
                }
            }
            else if (path == "/api/parties/verify-admin" && requestBody != null)
            {
                try
                {
                    var request = _jsonSerializer.DeserializeFromString<Dictionary<string, object>>(requestBody);
                    var adminPassword = request.ContainsKey("adminPassword") ? request["adminPassword"]?.ToString() : null;
                    var config = Plugin.Instance.Configuration;

                    if (string.IsNullOrEmpty(config.AdminPasswordHash) || string.IsNullOrEmpty(adminPassword) || 
                        !PasswordHelper.VerifyPassword(adminPassword, config.AdminPasswordHash))
                    {
                        response.StatusCode = (int)HttpStatusCode.Unauthorized;
                        await WriteResponse(response, "{\"error\":\"Invalid admin password\"}");
                        return;
                    }

                    response.StatusCode = (int)HttpStatusCode.OK;
                    response.ContentType = "application/json";
                    await WriteResponse(response, "{\"success\":true}");
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("[ExternalWebServer] Error verifying admin password", ex);
                    response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    await WriteResponse(response, "{\"error\":\"Failed to verify password\"}");
                }
            }
            else if (path == "/api/parties" && requestBody == null)
            {
                var password = queryString["password"];
                var config = Plugin.Instance.Configuration;
                var parties = new List<object>();

                foreach (var party in config.WatchParties)
                {
                    if (!string.IsNullOrEmpty(party.PasswordHash))
                    {
                        if (string.IsNullOrEmpty(password) || !PasswordHelper.VerifyPassword(password, party.PasswordHash))
                        {
                            continue;
                        }
                    }

                    parties.Add(new
                    {
                        Id = party.Id,
                        ItemId = party.ItemId,
                        ItemName = party.ItemName,
                        ItemType = party.ItemType,
                        IsActive = party.IsActive,
                        IsWaitingRoom = party.IsWaitingRoom,
                        ParticipantCount = 0,
                        MaxParticipants = party.MaxParticipants,
                        HostUserName = !string.IsNullOrEmpty(party.MasterUserId) ? party.MasterUserId : "Not set",
                        CurrentPositionTicks = party.CurrentPositionTicks,
                        IsPlaying = party.IsPlaying,
                        RequiresPassword = !string.IsNullOrEmpty(party.PasswordHash)
                    });
                }

                var json = _jsonSerializer.SerializeToString(new { Parties = parties });
                response.StatusCode = (int)HttpStatusCode.OK;
                response.ContentType = "application/json";
                await WriteResponse(response, json);
            }
            else if (path == "/api/parties/create" && requestBody != null)
            {
                try
                {
                    var request = _jsonSerializer.DeserializeFromString<Dictionary<string, object>>(requestBody);
                    var adminPassword = request.ContainsKey("adminPassword") ? request["adminPassword"]?.ToString() : null;
                    var config = Plugin.Instance.Configuration;

                    if (string.IsNullOrEmpty(config.AdminPasswordHash) || string.IsNullOrEmpty(adminPassword) || 
                        !PasswordHelper.VerifyPassword(adminPassword, config.AdminPasswordHash))
                    {
                        response.StatusCode = (int)HttpStatusCode.Unauthorized;
                        await WriteResponse(response, "{\"error\":\"Invalid admin password\"}");
                        return;
                    }

                    var allowedUserIds = new List<string>();
                    if (request.ContainsKey("allowedUserIds") && request["allowedUserIds"] != null)
                    {
                        var userIdsArray = request["allowedUserIds"] as object[];
                        if (userIdsArray != null)
                        {
                            allowedUserIds = userIdsArray.Select(x => x?.ToString()).Where(x => !string.IsNullOrEmpty(x)).ToList();
                        }
                    }
                    
                    // Get target library path from Emby API
                    string targetLibraryPath = "";
                    var targetLibraryId = request.ContainsKey("targetLibraryId") ? request["targetLibraryId"]?.ToString() : "";
                    if (!string.IsNullOrEmpty(targetLibraryId) && !string.IsNullOrEmpty(config.EmbyApiKey))
                    {
                        try
                        {
                            using (var client = new System.Net.Http.HttpClient())
                            {
                                var libraryUrl = $"http://localhost:8096/Library/MediaFolders?api_key={Uri.EscapeDataString(config.EmbyApiKey)}";
                                var libraryResponse = await client.GetAsync(libraryUrl);
                                if (libraryResponse.IsSuccessStatusCode)
                                {
                                    var libraryContent = await libraryResponse.Content.ReadAsStringAsync();
                                    var libraryData = _jsonSerializer.DeserializeFromString<Dictionary<string, object>>(libraryContent);
                                    if (libraryData.ContainsKey("Items") && libraryData["Items"] is object[] items)
                                    {
                                        foreach (var item in items)
                                        {
                                            var lib = item as Dictionary<string, object>;
                                            if (lib != null && lib.ContainsKey("Id") && lib["Id"]?.ToString() == targetLibraryId)
                                            {
                                                if (lib.ContainsKey("Locations") && lib["Locations"] is object[] locations && locations.Length > 0)
                                                {
                                                    targetLibraryPath = locations[0]?.ToString() ?? "";
                                                    break;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.ErrorException("[ExternalWebServer] Error getting library path", ex);
                        }
                    }

                    var newParty = new WatchPartyItem
                    {
                        Id = Guid.NewGuid().ToString(),
                        LibraryId = request.ContainsKey("libraryId") ? request["libraryId"]?.ToString() : "",
                        ItemId = request.ContainsKey("itemId") ? request["itemId"]?.ToString() : "",
                        ItemName = request.ContainsKey("itemName") ? request["itemName"]?.ToString() : "New Party",
                        ItemType = request.ContainsKey("itemType") ? request["itemType"]?.ToString() : "Movie",
                        SeriesId = request.ContainsKey("seriesId") ? request["seriesId"]?.ToString() : null,
                        SeasonId = request.ContainsKey("seasonId") ? request["seasonId"]?.ToString() : null,
                        CollectionName = request.ContainsKey("collectionName") ? request["collectionName"]?.ToString() : "Watch Party",
                        TargetLibraryId = targetLibraryId,
                        TargetLibraryPath = targetLibraryPath,
                        IsActive = request.ContainsKey("isActive") ? Convert.ToBoolean(request["isActive"]) : false,
                        CurrentPositionTicks = 0,
                        IsPlaying = false,
                        MaxParticipants = request.ContainsKey("maxParticipants") ? Convert.ToInt32(request["maxParticipants"]) : 50,
                        AllowedUserIds = allowedUserIds,
                        MasterUserId = request.ContainsKey("masterUserId") ? request["masterUserId"]?.ToString() : "",
                        IsWaitingRoom = request.ContainsKey("isWaitingRoom") ? Convert.ToBoolean(request["isWaitingRoom"]) : false,
                        AutoStartWhenReady = request.ContainsKey("autoStartWhenReady") ? Convert.ToBoolean(request["autoStartWhenReady"]) : false,
                        MinReadyCount = request.ContainsKey("minReadyCount") ? Convert.ToInt32(request["minReadyCount"]) : 1,
                        PauseControl = request.ContainsKey("pauseControl") ? request["pauseControl"]?.ToString() : "Anyone",
                        HostOnlySeek = request.ContainsKey("hostOnlySeek") ? Convert.ToBoolean(request["hostOnlySeek"]) : false,
                        LockSeekAhead = request.ContainsKey("lockSeekAhead") ? Convert.ToBoolean(request["lockSeekAhead"]) : false,
                        SyncToleranceSeconds = request.ContainsKey("syncToleranceSeconds") ? Convert.ToInt32(request["syncToleranceSeconds"]) : 10,
                        MaxBufferThresholdSeconds = request.ContainsKey("maxBufferThresholdSeconds") ? Convert.ToInt32(request["maxBufferThresholdSeconds"]) : 30,
                        AutoKickInactiveMinutes = request.ContainsKey("autoKickInactiveMinutes") ? Convert.ToBoolean(request["autoKickInactiveMinutes"]) : false,
                        InactiveTimeoutMinutes = request.ContainsKey("inactiveTimeoutMinutes") ? Convert.ToInt32(request["inactiveTimeoutMinutes"]) : 15,
                        CreatedDate = DateTime.UtcNow
                    };

                    var partyPassword = request.ContainsKey("password") ? request["password"]?.ToString() : null;
                    if (!string.IsNullOrEmpty(partyPassword))
                    {
                        newParty.PasswordHash = PasswordHelper.HashPassword(partyPassword);
                    }

                    config.WatchParties.Add(newParty);
                    Plugin.Instance.UpdateConfiguration(config);

                    var resultJson = _jsonSerializer.SerializeToString(new { success = true, partyId = newParty.Id });
                    response.StatusCode = (int)HttpStatusCode.OK;
                    response.ContentType = "application/json";
                    await WriteResponse(response, resultJson);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("[ExternalWebServer] Error creating party", ex);
                    response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    await WriteResponse(response, "{\"error\":\"Failed to create party\"}");
                }
            }
            else if (path == "/api/parties/delete" && requestBody != null)
            {
                try
                {
                    var request = _jsonSerializer.DeserializeFromString<Dictionary<string, object>>(requestBody);
                    var partyId = request.ContainsKey("partyId") ? request["partyId"]?.ToString() : null;
                    var adminPassword = request.ContainsKey("adminPassword") ? request["adminPassword"]?.ToString() : null;
                    var config = Plugin.Instance.Configuration;

                    if (string.IsNullOrEmpty(config.AdminPasswordHash) || string.IsNullOrEmpty(adminPassword) || 
                        !PasswordHelper.VerifyPassword(adminPassword, config.AdminPasswordHash))
                    {
                        response.StatusCode = (int)HttpStatusCode.Unauthorized;
                        await WriteResponse(response, "{\"error\":\"Invalid admin password\"}");
                        return;
                    }

                    if (string.IsNullOrEmpty(partyId))
                    {
                        response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await WriteResponse(response, "{\"error\":\"Party ID is required\"}");
                        return;
                    }

                    var party = config.WatchParties.FirstOrDefault(p => p.Id == partyId);
                    if (party == null)
                    {
                        response.StatusCode = (int)HttpStatusCode.NotFound;
                        await WriteResponse(response, "{\"error\":\"Party not found\"}");
                        return;
                    }

                    config.WatchParties.Remove(party);
                    Plugin.Instance.UpdateConfiguration(config);

                    var resultJson = _jsonSerializer.SerializeToString(new { success = true });
                    response.StatusCode = (int)HttpStatusCode.OK;
                    response.ContentType = "application/json";
                    await WriteResponse(response, resultJson);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException("[ExternalWebServer] Error deleting party", ex);
                    response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    await WriteResponse(response, "{\"error\":\"Failed to delete party\"}");
                }
            }
            else
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                await WriteResponse(response, "{\"error\":\"Not found\"}");
            }
        }

        private string GetContentType(string path)
        {
            if (path.EndsWith(".html")) return "text/html";
            if (path.EndsWith(".js")) return "application/javascript";
            if (path.EndsWith(".css")) return "text/css";
            if (path.EndsWith(".json")) return "application/json";
            return "application/octet-stream";
        }

        private async Task WriteResponse(HttpListenerResponse response, string content)
        {
            var buffer = Encoding.UTF8.GetBytes(content);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        }
    }
}
