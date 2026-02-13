using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;

namespace WatchPartyForEmby.Api
{
    [Route("/WatchParty/Sync", "GET", Summary = "Get watch party sync state")]
    public class WatchPartySyncRequest : IReturn<WatchPartySyncResponse>
    {
        [ApiMember(Name = "UserId", Description = "User ID", IsRequired = true)]
        public string UserId { get; set; }
    }

    public class WatchPartySyncResponse
    {
        public bool IsPartyActive { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public string ItemType { get; set; }
        public long CurrentPositionTicks { get; set; }
        public bool IsPlaying { get; set; }
        public int MaxParticipants { get; set; }
    }

    [Route("/WatchParty/Info", "GET", Summary = "Get watch party information")]
    public class WatchPartyInfoRequest : IReturn<WatchPartyInfoResponse>
    {
    }

    public class WatchPartyInfoResponse
    {
        public bool IsPartyActive { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public string ItemType { get; set; }
        public long CurrentPositionTicks { get; set; }
        public bool IsPlaying { get; set; }
    }

    [Route("/WatchParty/List", "GET", Summary = "Get all watch parties")]
    public class WatchPartyListRequest : IReturn<WatchPartyListResponse>
    {
        [ApiMember(Name = "UserId", Description = "User ID to filter accessible parties", IsRequired = false)]
        public string UserId { get; set; }
        
        [ApiMember(Name = "Password", Description = "Party password for external access", IsRequired = false)]
        public string Password { get; set; }
    }

    public class WatchPartyListResponse
    {
        public List<WatchPartyInfo> Parties { get; set; }
    }

    public class WatchPartyInfo
    {
        public string Id { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public string ItemType { get; set; }
        public bool IsActive { get; set; }
        public bool IsWaitingRoom { get; set; }
        public int ParticipantCount { get; set; }
        public int MaxParticipants { get; set; }
        public string HostUserName { get; set; }
        public long CurrentPositionTicks { get; set; }
        public bool IsPlaying { get; set; }
        public bool RequiresPassword { get; set; }
    }

    [Route("/WatchParty/{Id}/Participants", "GET", Summary = "Get party participants")]
    public class PartyParticipantsRequest : IReturn<PartyParticipantsResponse>
    {
        [ApiMember(Name = "Id", Description = "Party ID", IsRequired = true)]
        public string Id { get; set; }
    }

    public class PartyParticipantsResponse
    {
        public List<ParticipantInfo> Participants { get; set; }
    }

    public class ParticipantInfo
    {
        public string UserId { get; set; }
        public string UserName { get; set; }
        public bool IsHost { get; set; }
        public bool IsReady { get; set; }
        public bool IsBuffering { get; set; }
        public long CurrentPositionTicks { get; set; }
        public DateTime LastActivityAt { get; set; }
    }

    [Route("/WatchParty/{Id}/Ready", "POST", Summary = "Mark user as ready")]
    public class SetReadyRequest : IReturnVoid
    {
        [ApiMember(Name = "Id", Description = "Party ID", IsRequired = true)]
        public string Id { get; set; }
        
        [ApiMember(Name = "UserId", Description = "User ID", IsRequired = true)]
        public string UserId { get; set; }
        
        [ApiMember(Name = "IsReady", Description = "Ready state", IsRequired = true)]
        public bool IsReady { get; set; }
    }

    [Route("/WatchParty/{Id}/Start", "POST", Summary = "Start party from waiting room")]
    public class StartPartyRequest : IReturnVoid
    {
        [ApiMember(Name = "Id", Description = "Party ID", IsRequired = true)]
        public string Id { get; set; }
        
        [ApiMember(Name = "UserId", Description = "User ID (must be host)", IsRequired = true)]
        public string UserId { get; set; }
    }

    [Route("/WatchParty/Users", "GET", Summary = "Get all Emby users")]
    public class GetUsersRequest : IReturn<GetUsersResponse>
    {
    }

    public class GetUsersResponse
    {
        public List<EmbyUserInfo> Users { get; set; }
    }

    public class EmbyUserInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    [Route("/WatchPartyForEmby/Images/{ImageName}", "GET", Summary = "Gets a plugin image resource")]
    public class GetImageRequest : IReturn<Stream>
    {
        [ApiMember(Name = "ImageName", Description = "Image file name", IsRequired = true)]
        public string ImageName { get; set; }
    }

    public class WatchPartyService : IService
    {
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IUserManager _userManager;

        public WatchPartyService(IJsonSerializer jsonSerializer, IUserManager userManager)
        {
            _jsonSerializer = jsonSerializer;
            _userManager = userManager;
        }

        public object Get(WatchPartySyncRequest request)
        {
            var config = Plugin.Instance.Configuration;
            
            return new WatchPartySyncResponse
            {
                IsPartyActive = config.IsPartyActive,
                ItemId = config.SelectedItemId ?? "",
                ItemName = config.SelectedItemName ?? "",
                ItemType = config.SelectedItemType ?? "",
                CurrentPositionTicks = config.CurrentPositionTicks,
                IsPlaying = config.IsPlaying,
                MaxParticipants = config.MaxParticipants
            };
        }

        public object Get(WatchPartyInfoRequest request)
        {
            var config = Plugin.Instance.Configuration;
            
            return new WatchPartyInfoResponse
            {
                IsPartyActive = config.IsPartyActive,
                ItemId = config.SelectedItemId ?? "",
                ItemName = config.SelectedItemName ?? "",
                ItemType = config.SelectedItemType ?? "",
                CurrentPositionTicks = config.CurrentPositionTicks,
                IsPlaying = config.IsPlaying
            };
        }

        public object Get(WatchPartyListRequest request)
        {
            var config = Plugin.Instance.Configuration;
            var parties = new List<WatchPartyInfo>();

            foreach (var party in config.WatchParties)
            {
                if (!string.IsNullOrEmpty(party.Password))
                {
                    if (string.IsNullOrEmpty(request.Password) || request.Password != party.Password)
                    {
                        continue;
                    }
                }
                
                if (!string.IsNullOrEmpty(request.UserId) && 
                    party.AllowedUserIds != null && 
                    party.AllowedUserIds.Count > 0 && 
                    !party.AllowedUserIds.Contains(request.UserId))
                {
                    continue;
                }

                string hostUserName = "Not set";
                if (!string.IsNullOrEmpty(party.HostUserId))
                {
                    var hostUser = _userManager.GetUserById(party.HostUserId);
                    if (hostUser != null)
                    {
                        hostUserName = hostUser.Name;
                    }
                }
                
                parties.Add(new WatchPartyInfo
                {
                    Id = party.Id,
                    ItemId = party.ItemId,
                    ItemName = party.ItemName,
                    ItemType = party.ItemType,
                    IsActive = party.IsActive,
                    IsWaitingRoom = party.IsWaitingRoom,
                    ParticipantCount = 0,
                    MaxParticipants = party.MaxParticipants,
                    HostUserName = hostUserName,
                    CurrentPositionTicks = party.CurrentPositionTicks,
                    IsPlaying = party.IsPlaying,
                    RequiresPassword = !string.IsNullOrEmpty(party.Password)
                });
            }

            return new WatchPartyListResponse { Parties = parties };
        }

        public object Get(PartyParticipantsRequest request)
        {
            return new PartyParticipantsResponse
            {
                Participants = new List<ParticipantInfo>()
            };
        }

        public void Post(SetReadyRequest request)
        {
            var config = Plugin.Instance.Configuration;
            var party = config.WatchParties.FirstOrDefault(p => p.Id == request.Id);
            
            if (party == null)
            {
                throw new ArgumentException($"Party {request.Id} not found");
            }
        }

        public void Post(StartPartyRequest request)
        {
            var config = Plugin.Instance.Configuration;
            var party = config.WatchParties.FirstOrDefault(p => p.Id == request.Id);
            
            if (party == null)
            {
                throw new ArgumentException($"Party {request.Id} not found");
            }

            if (party.HostUserId != request.UserId)
            {
                throw new UnauthorizedAccessException("Only the host can start the party");
            }

            party.IsWaitingRoom = false;
            party.IsPlaying = true;
            Plugin.Instance.SaveConfiguration();
        }

        public object Get(GetUsersRequest request)
        {
            return new GetUsersResponse
            {
                Users = new List<EmbyUserInfo>()
            };
        }

        public Stream Get(GetImageRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.ImageName))
                {
                    return Stream.Null;
                }

                var assembly = typeof(Plugin).GetTypeInfo().Assembly;
                var resourceName = $"WatchPartyForEmby.images.{request.ImageName}";

                var stream = assembly.GetManifestResourceStream(resourceName);

                if (stream == null)
                {
                    return Stream.Null;
                }

                return stream;
            }
            catch (Exception)
            {
                return Stream.Null;
            }
        }
    }
}
