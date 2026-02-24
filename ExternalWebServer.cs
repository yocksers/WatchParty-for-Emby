using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
        private readonly string _listenAddress;
        private readonly Dictionary<string, SessionToken> _activeSessions = new Dictionary<string, SessionToken>();
        private readonly Dictionary<string, RateLimitEntry> _rateLimits = new Dictionary<string, RateLimitEntry>();
        private readonly object _sessionLock = new object();
        private readonly object _rateLimitLock = new object();

        public ExternalWebServer(ILogger logger, IJsonSerializer jsonSerializer, int port, string listenAddress = "localhost")
        {
            _logger = logger;
            _jsonSerializer = jsonSerializer;
            _port = port;
            _listenAddress = listenAddress;
            _cancellationTokenSource = new CancellationTokenSource();

            _listener = new HttpListener();
            
            if (listenAddress == "*" || listenAddress == "0.0.0.0")
            {
                _listener.Prefixes.Add($"http://*:{port}/");
                _logger.Warn($"[ExternalWebServer] WARNING: Binding to all network interfaces. This may expose sensitive data.");
            }
            else
            {
                _listener.Prefixes.Add($"http://{listenAddress}:{port}/");
            }
        }

        public string Start()
        {
            try
            {
                _listener.Start();
                var prefix = _listener.Prefixes.First();
                _logger.Info($"[ExternalWebServer] Started and listening on {prefix}");
                Task.Run(() => Listen(_cancellationTokenSource.Token));
                Task.Run(() => CleanupExpiredSessions());
                return $"Running on {_listenAddress}:{_port}";
            }
            catch (HttpListenerException ex) when (ex.ErrorCode == 5)
            {
                var errorMsg = _listenAddress == "localhost" 
                    ? $"Error: Access Denied. On Windows, run as Administrator: netsh http add urlacl url=http://{_listenAddress}:{_port}/ user=\"Everyone\""
                    : $"Error: Access Denied. Run as Administrator: netsh http add urlacl url=http://{_listenAddress}:{_port}/ user=\"Everyone\"";
                _logger.Error(errorMsg);
                return errorMsg;
            }
            catch (Exception ex)
            {
                var errorMsg = "[ExternalWebServer] Failed to start";
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
            lock (_sessionLock)
            {
                _activeSessions.Clear();
            }
            _logger.Info("[ExternalWebServer] Stopped.");
        }

        private async Task CleanupExpiredSessions()
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), _cancellationTokenSource.Token);
                    
                    lock (_sessionLock)
                    {
                        var expiredTokens = _activeSessions
                            .Where(kvp => !kvp.Value.IsValid())
                            .Select(kvp => kvp.Key)
                            .ToList();
                        
                        foreach (var token in expiredTokens)
                        {
                            _activeSessions.Remove(token);
                        }
                        
                        if (expiredTokens.Count > 0)
                        {
                            _logger.Debug($"[ExternalWebServer] Cleaned up {expiredTokens.Count} expired sessions");
                        }
                    }
                }
                catch (TaskCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.ErrorException("[ExternalWebServer] Error cleaning up sessions", ex);
                }
            }
        }

        private bool CheckRateLimit(string ipAddress)
        {
            var config = Plugin.Instance.Configuration;
            if (config.RateLimitRequestsPerMinute <= 0) return true;

            lock (_rateLimitLock)
            {
                if (!_rateLimits.ContainsKey(ipAddress))
                {
                    _rateLimits[ipAddress] = new RateLimitEntry
                    {
                        RequestCount = 1,
                        WindowStart = DateTime.UtcNow
                    };
                    return true;
                }

                var entry = _rateLimits[ipAddress];

                if (entry.BlockedUntil.HasValue)
                {
                    if (DateTime.UtcNow < entry.BlockedUntil.Value)
                    {
                        return false;
                    }
                    entry.BlockedUntil = null;
                    entry.RequestCount = 0;
                    entry.WindowStart = DateTime.UtcNow;
                }

                if ((DateTime.UtcNow - entry.WindowStart).TotalMinutes >= 1)
                {
                    entry.RequestCount = 1;
                    entry.WindowStart = DateTime.UtcNow;
                    return true;
                }

                entry.RequestCount++;

                if (entry.RequestCount > config.RateLimitRequestsPerMinute)
                {
                    entry.BlockedUntil = DateTime.UtcNow.AddMinutes(config.RateLimitBlockDurationMinutes);
                    _logger.Warn($"[ExternalWebServer] Rate limit exceeded for {ipAddress}. Blocked for {config.RateLimitBlockDurationMinutes} minutes.");
                    return false;
                }

                return true;
            }
        }

        private string GenerateSessionToken()
        {
            var tokenBytes = new byte[32];
            RandomNumberGenerator.Fill(tokenBytes);
            return Convert.ToBase64String(tokenBytes);
        }

        private string CreateSession(string ipAddress)
        {
            var config = Plugin.Instance.Configuration;
            var token = GenerateSessionToken();
            
            lock (_sessionLock)
            {
                _activeSessions[token] = new SessionToken
                {
                    Token = token,
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(config.SessionExpirationMinutes),
                    IpAddress = ipAddress
                };
            }
            
            _logger.Debug($"[ExternalWebServer] Created session for {ipAddress}");
            return token;
        }

        private bool ValidateSession(string token, string ipAddress)
        {
            if (string.IsNullOrEmpty(token)) return false;

            lock (_sessionLock)
            {
                if (!_activeSessions.TryGetValue(token, out var session))
                    return false;

                if (!session.IsValid())
                {
                    _activeSessions.Remove(token);
                    return false;
                }

                if (session.IpAddress != ipAddress)
                {
                    _logger.Warn($"[ExternalWebServer] Session token used from different IP. Expected: {session.IpAddress}, Got: {ipAddress}");
                    return false;
                }

                return true;
            }
        }

        private bool ValidateInput(string input, int maxLength = 500)
        {
            if (string.IsNullOrEmpty(input)) return true;
            if (input.Length > maxLength) return false;
            
            // Check for null bytes and control characters
            return !input.Any(c => c == '\0' || (char.IsControl(c) && c != '\r' && c != '\n' && c != '\t'));
        }

        private string SanitizeErrorMessage(string message)
        {
            // Remove sensitive information from error messages
            message = Regex.Replace(message, @"[A-Za-z]:\\[^\s]+", "[path]");
            message = Regex.Replace(message, @"/[^\s]+", "[path]");
            message = Regex.Replace(message, @"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", "[ip]");
            return message;
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

            var ipAddress = request.RemoteEndPoint?.Address?.ToString() ?? "unknown";

            if (!CheckRateLimit(ipAddress))
            {
                response.StatusCode = (int)HttpStatusCode.TooManyRequests;
                await WriteResponse(response, "{\"error\":\"Rate limit exceeded. Please try again later.\"}");
                response.OutputStream.Close();
                return;
            }

            try
            {
                var config = Plugin.Instance.Configuration;
                var origin = request.Headers["Origin"];
                
                // Set CORS headers
                if (!string.IsNullOrEmpty(config.AllowedCorsOrigins))
                {
                    var allowedOrigins = config.AllowedCorsOrigins.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(o => o.Trim())
                        .ToList();
                    
                    if (allowedOrigins.Contains("*"))
                    {
                        response.AddHeader("Access-Control-Allow-Origin", "*");
                    }
                    else if (!string.IsNullOrEmpty(origin) && allowedOrigins.Contains(origin))
                    {
                        response.AddHeader("Access-Control-Allow-Origin", origin);
                        response.AddHeader("Vary", "Origin");
                    }
                }
                else
                {
                    // Default to localhost only
                    if (!string.IsNullOrEmpty(origin) && 
                        (origin.StartsWith("http://localhost") || origin.StartsWith("http://127.0.0.1")))
                    {
                        response.AddHeader("Access-Control-Allow-Origin", origin);
                        response.AddHeader("Vary", "Origin");
                    }
                }
                
                response.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.AddHeader("Access-Control-Allow-Headers", "Content-Type, X-Session-Token, X-Auth-Password");
                response.AddHeader("Access-Control-Expose-Headers", "X-Session-Token");

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
                        
                        if (requestBody.Length > 1048576) // 1MB limit
                        {
                            response.StatusCode = (int)HttpStatusCode.RequestEntityTooLarge;
                            await WriteResponse(response, "{\"error\":\"Request too large\"}");
                            response.OutputStream.Close();
                            return;
                        }
                        
                        await HandleApiRequest(request, response, requestBody, ipAddress);
                    }
                    else
                    {
                        await HandleApiRequest(request, response, null, ipAddress);
                    }
                    return;
                }

                await HandleFileRequest(request.Url.AbsolutePath, response);
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[ExternalWebServer] Error during request:", ex);
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await WriteResponse(response, "{\"error\":\"An internal error occurred\"}");
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

        private async Task HandleApiRequest(HttpListenerRequest request, HttpListenerResponse response, string requestBody, string ipAddress)
        {
            var path = request.Url.AbsolutePath;
            var queryString = request.QueryString;
            var sessionToken = request.Headers["X-Session-Token"];
            var authPassword = request.Headers["X-Auth-Password"];

            // Public endpoints (no authentication required)
            if (path == "/api/auth/login" && requestBody != null)
            {
                await HandleLogin(response, requestBody, ipAddress);
                return;
            }
            else if (path == "/api/config/external-url" && requestBody == null)
            {
                // Public endpoint - no auth required
                var config = Plugin.Instance.Configuration;
                var result = new
                {
                    externalServerUrl = config.ExternalServerUrl ?? ""
                };
                
                var json = _jsonSerializer.SerializeToString(result);
                response.StatusCode = (int)HttpStatusCode.OK;
                response.ContentType = "application/json";
                await WriteResponse(response, json);
                return;
            }
            else if (path == "/api/users/lookup" && requestBody != null)
            {
                // Public endpoint - only returns username for display purposes
                await HandleUserLookup(response, requestBody);
                return;
            }

            // Protected endpoints - require session token
            if (path.StartsWith("/api/emby/"))
            {
                if (!ValidateSession(sessionToken, ipAddress))
                {
                    response.StatusCode = (int)HttpStatusCode.Unauthorized;
                    await WriteResponse(response, "{\"error\":\"Invalid or expired session\"}");
                    return;
                }

                await HandleEmbyProxy(request, response, requestBody);
                return;
            }
            else if (path == "/api/config/strm-library" && requestBody == null)
            {
                if (!ValidateSession(sessionToken, ipAddress))
                {
                    response.StatusCode = (int)HttpStatusCode.Unauthorized;
                    await WriteResponse(response, "{\"error\":\"Invalid or expired session\"}");
                    return;
                }

                var config = Plugin.Instance.Configuration;
                var result = new
                {
                    targetLibraryId = config.StrmTargetLibraryId ?? "",
                    targetLibraryName = config.StrmTargetLibraryName ?? ""
                };
                
                var json = _jsonSerializer.SerializeToString(result);
                response.StatusCode = (int)HttpStatusCode.OK;
                response.ContentType = "application/json";
                await WriteResponse(response, json);
            }
            else if (path == "/api/parties" && requestBody == null)
            {
                // Party password moved to header
                var password = request.Headers["X-Party-Password"];
                await HandleGetParties(response, password);
            }
            else if (path == "/api/parties/create" && requestBody != null)
            {
                if (!ValidateSession(sessionToken, ipAddress))
                {
                    response.StatusCode = (int)HttpStatusCode.Unauthorized;
                    await WriteResponse(response, "{\"error\":\"Invalid or expired session\"}");
                    return;
                }

                await HandleCreateParty(response, requestBody);
            }
            else if (path == "/api/parties/delete" && requestBody != null)
            {
                if (!ValidateSession(sessionToken, ipAddress))
                {
                    response.StatusCode = (int)HttpStatusCode.Unauthorized;
                    await WriteResponse(response, "{\"error\":\"Invalid or expired session\"}");
                    return;
                }

                await HandleDeleteParty(response, requestBody);
            }
            else if (path == "/api/auth/logout" && sessionToken != null)
            {
                lock (_sessionLock)
                {
                    _activeSessions.Remove(sessionToken);
                }
                response.StatusCode = (int)HttpStatusCode.OK;
                await WriteResponse(response, "{\"success\":true}");
            }
            else
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                await WriteResponse(response, "{\"error\":\"Endpoint not found\"}");
            }
        }

        private async Task HandleLogin(HttpListenerResponse response, string requestBody, string ipAddress)
        {
            try
            {
                var request = _jsonSerializer.DeserializeFromString<Dictionary<string, object>>(requestBody);
                var adminPassword = request.ContainsKey("adminPassword") ? request["adminPassword"]?.ToString() : null;

                if (!ValidateInput(adminPassword, 200))
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteResponse(response, "{\"error\":\"Invalid input\"}");
                    return;
                }

                var config = Plugin.Instance.Configuration;

                if (string.IsNullOrEmpty(config.AdminPasswordHash) || string.IsNullOrEmpty(adminPassword) || 
                    !PasswordHelper.VerifyPassword(adminPassword, config.AdminPasswordHash))
                {
                    // Add delay to prevent brute force
                    await Task.Delay(1000);
                    response.StatusCode = (int)HttpStatusCode.Unauthorized;
                    await WriteResponse(response, "{\"error\":\"Invalid credentials\"}");
                    return;
                }

                var token = CreateSession(ipAddress);
                
                response.AddHeader("X-Session-Token", token);
                response.StatusCode = (int)HttpStatusCode.OK;
                response.ContentType = "application/json";
                await WriteResponse(response, _jsonSerializer.SerializeToString(new { 
                    success = true, 
                    token = token,
                    expiresIn = config.SessionExpirationMinutes * 60
                }));
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[ExternalWebServer] Error during login", ex);
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await WriteResponse(response, "{\"error\":\"Authentication failed\"}");
            }
        }

        private async Task HandleEmbyProxy(HttpListenerRequest request, HttpListenerResponse response, string requestBody)
        {
            try
            {
                var config = Plugin.Instance.Configuration;
                var apiKey = config.EmbyApiKey;
                
                if (string.IsNullOrEmpty(apiKey))
                {
                    response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                    await WriteResponse(response, "{\"error\":\"Service not configured\"}");
                    return;
                }
                
                var embyPath = request.Url.AbsolutePath.Substring("/api/emby".Length);
                using (var client = new System.Net.Http.HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                    
                    var embyUrl = $"http://localhost:8096/emby{embyPath}";
                    var queryParams = new List<string>();
                    
                    if (request.QueryString.Count > 0)
                    {
                        foreach (var key in request.QueryString.AllKeys)
                        {
                            if (!ValidateInput(key, 100) || !ValidateInput(request.QueryString[key], 500))
                            {
                                response.StatusCode = (int)HttpStatusCode.BadRequest;
                                await WriteResponse(response, "{\"error\":\"Invalid query parameters\"}");
                                return;
                            }
                            queryParams.Add($"{key}={Uri.EscapeDataString(request.QueryString[key])}");
                        }
                    }
                    
                    if (queryParams.Count > 0)
                    {
                        embyUrl += "?" + string.Join("&", queryParams);
                    }
                    
                    var httpRequest = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, embyUrl);
                    httpRequest.Headers.Add("X-Emby-Token", apiKey);
                    
                    var embyResponse = await client.SendAsync(httpRequest);
                    var content = await embyResponse.Content.ReadAsStringAsync();
                    
                    response.StatusCode = (int)embyResponse.StatusCode;
                    response.ContentType = "application/json";
                    await WriteResponse(response, content);
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException("[ExternalWebServer] Error proxying to Emby", ex);
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await WriteResponse(response, "{\"error\":\"Proxy request failed\"}");
            }
        }

        private async Task HandleUserLookup(HttpListenerResponse response, string requestBody)
        {
            try
            {
                var request = _jsonSerializer.DeserializeFromString<Dictionary<string, object>>(requestBody);
                var userId = request.ContainsKey("userId") ? request["userId"]?.ToString() : null;
                
                if (!ValidateInput(userId, 100))
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteResponse(response, "{\"error\":\"Invalid input\"}");
                    return;
                }
                
                if (string.IsNullOrEmpty(userId))
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteResponse(response, "{\"error\":\"userId is required\"}");
                    return;
                }
                
                var config = Plugin.Instance.Configuration;
                var embyServerUrl = config.ExternalServerUrl;
                if (string.IsNullOrEmpty(embyServerUrl))
                {
                    embyServerUrl = "http://localhost:8096";
                }
                
                var apiKey = config.EmbyApiKey;
                
                using (var httpClient = new System.Net.Http.HttpClient())
                {
                    httpClient.Timeout = TimeSpan.FromSeconds(10);
                    var httpRequest = new System.Net.Http.HttpRequestMessage(
                        System.Net.Http.HttpMethod.Get, 
                        $"{embyServerUrl}/emby/Users/{Uri.EscapeDataString(userId)}"
                    );
                    httpRequest.Headers.Add("X-Emby-Token", apiKey);
                    
                    var userResponse = await httpClient.SendAsync(httpRequest);
                    var userJson = await userResponse.Content.ReadAsStringAsync();
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
                _logger.ErrorException("[ExternalWebServer] Error looking up user", ex);
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await WriteResponse(response, "{\"error\":\"User lookup failed\"}");
            }
        }

        private async Task HandleGetParties(HttpListenerResponse response, string password)
        {
            try
            {
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
            catch (Exception ex)
            {
                _logger.ErrorException("[ExternalWebServer] Error getting parties", ex);
                response.StatusCode = (int)HttpStatusCode.InternalServerError;
                await WriteResponse(response, "{\"error\":\"Failed to retrieve parties\"}");
            }
        }

        private async Task HandleCreateParty(HttpListenerResponse response, string requestBody)
        {
            try
            {
                var request = _jsonSerializer.DeserializeFromString<Dictionary<string, object>>(requestBody);
                var config = Plugin.Instance.Configuration;

                // Validate inputs
                var itemName = request.ContainsKey("itemName") ? request["itemName"]?.ToString() : "New Party";
                if (!ValidateInput(itemName, 200))
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteResponse(response, "{\"error\":\"Invalid item name\"}");
                    return;
                }

                var allowedUserIds = new List<string>();
                if (request.ContainsKey("allowedUserIds") && request["allowedUserIds"] != null)
                {
                    var userIdsArray = request["allowedUserIds"] as object[];
                    if (userIdsArray != null)
                    {
                        allowedUserIds = userIdsArray
                            .Select(x => x?.ToString())
                            .Where(x => !string.IsNullOrEmpty(x) && ValidateInput(x, 100))
                            .ToList();
                    }
                }
                
                // Get target library path from Emby API
                string targetLibraryPath = "";
                var targetLibraryId = request.ContainsKey("targetLibraryId") ? request["targetLibraryId"]?.ToString() : "";
                if (!string.IsNullOrEmpty(targetLibraryId) && ValidateInput(targetLibraryId, 100) && !string.IsNullOrEmpty(config.EmbyApiKey))
                {
                    try
                    {
                        using (var client = new System.Net.Http.HttpClient())
                        {
                            client.Timeout = TimeSpan.FromSeconds(10);
                            var httpRequest = new System.Net.Http.HttpRequestMessage(
                                System.Net.Http.HttpMethod.Get, 
                                "http://localhost:8096/Library/MediaFolders"
                            );
                            httpRequest.Headers.Add("X-Emby-Token", config.EmbyApiKey);
                            
                            var libraryResponse = await client.SendAsync(httpRequest);
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
                    LibraryId = request.ContainsKey("libraryId") && ValidateInput(request["libraryId"]?.ToString(), 100) 
                        ? request["libraryId"]?.ToString() : "",
                    ItemId = request.ContainsKey("itemId") && ValidateInput(request["itemId"]?.ToString(), 100) 
                        ? request["itemId"]?.ToString() : "",
                    ItemName = itemName,
                    ItemType = request.ContainsKey("itemType") && ValidateInput(request["itemType"]?.ToString(), 50) 
                        ? request["itemType"]?.ToString() : "Movie",
                    SeriesId = request.ContainsKey("seriesId") && ValidateInput(request["seriesId"]?.ToString(), 100) 
                        ? request["seriesId"]?.ToString() : null,
                    SeasonId = request.ContainsKey("seasonId") && ValidateInput(request["seasonId"]?.ToString(), 100) 
                        ? request["seasonId"]?.ToString() : null,
                    CollectionName = request.ContainsKey("collectionName") && ValidateInput(request["collectionName"]?.ToString(), 100) 
                        ? request["collectionName"]?.ToString() : "Watch Party",
                    TargetLibraryId = targetLibraryId,
                    TargetLibraryPath = targetLibraryPath,
                    IsActive = request.ContainsKey("isActive") ? Convert.ToBoolean(request["isActive"]) : false,
                    CurrentPositionTicks = 0,
                    IsPlaying = false,
                    MaxParticipants = request.ContainsKey("maxParticipants") ? 
                        Math.Min(Math.Max(Convert.ToInt32(request["maxParticipants"]), 1), 1000) : 50,
                    AllowedUserIds = allowedUserIds,
                    MasterUserId = request.ContainsKey("masterUserId") && ValidateInput(request["masterUserId"]?.ToString(), 100) 
                        ? request["masterUserId"]?.ToString() : "",
                    IsWaitingRoom = request.ContainsKey("isWaitingRoom") ? Convert.ToBoolean(request["isWaitingRoom"]) : false,
                    AutoStartWhenReady = request.ContainsKey("autoStartWhenReady") ? Convert.ToBoolean(request["autoStartWhenReady"]) : false,
                    MinReadyCount = request.ContainsKey("minReadyCount") ? 
                        Math.Max(Convert.ToInt32(request["minReadyCount"]), 1) : 1,
                    PauseControl = request.ContainsKey("pauseControl") && ValidateInput(request["pauseControl"]?.ToString(), 20) 
                        ? request["pauseControl"]?.ToString() : "Anyone",
                    HostOnlySeek = request.ContainsKey("hostOnlySeek") ? Convert.ToBoolean(request["hostOnlySeek"]) : false,
                    LockSeekAhead = request.ContainsKey("lockSeekAhead") ? Convert.ToBoolean(request["lockSeekAhead"]) : false,
                    SyncToleranceSeconds = request.ContainsKey("syncToleranceSeconds") ? 
                        Math.Max(Convert.ToInt32(request["syncToleranceSeconds"]), 1) : 10,
                    MaxBufferThresholdSeconds = request.ContainsKey("maxBufferThresholdSeconds") ? 
                        Math.Max(Convert.ToInt32(request["maxBufferThresholdSeconds"]), 1) : 30,
                    AutoKickInactiveMinutes = request.ContainsKey("autoKickInactiveMinutes") ? Convert.ToBoolean(request["autoKickInactiveMinutes"]) : false,
                    InactiveTimeoutMinutes = request.ContainsKey("inactiveTimeoutMinutes") ? 
                        Math.Max(Convert.ToInt32(request["inactiveTimeoutMinutes"]), 1) : 15,
                    CreatedDate = DateTime.UtcNow
                };

                var partyPassword = request.ContainsKey("password") ? request["password"]?.ToString() : null;
                if (!string.IsNullOrEmpty(partyPassword) && ValidateInput(partyPassword, 200))
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

        private async Task HandleDeleteParty(HttpListenerResponse response, string requestBody)
        {
            try
            {
                var request = _jsonSerializer.DeserializeFromString<Dictionary<string, object>>(requestBody);
                var partyId = request.ContainsKey("partyId") ? request["partyId"]?.ToString() : null;

                if (!ValidateInput(partyId, 100))
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteResponse(response, "{\"error\":\"Invalid input\"}");
                    return;
                }

                if (string.IsNullOrEmpty(partyId))
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await WriteResponse(response, "{\"error\":\"Party ID is required\"}");
                    return;
                }

                var config = Plugin.Instance.Configuration;
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
