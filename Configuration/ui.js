define([], function () {
    'use strict';

    class WatchPartyUI {
        populateLibraryDropdown(select, libraries) {
            select.innerHTML = '<option value="">-- Select library --</option>';
            
            libraries.forEach(library => {
                const option = document.createElement('option');
                option.value = library.Id;
                option.dataset.name = library.Name;
                option.dataset.path = library.Locations && library.Locations.length > 0 ? library.Locations[0] : '';
                option.textContent = library.Name;
                select.appendChild(option);
            });
        }

        populateContentDropdown(select, items) {
            select.innerHTML = '<option value="">-- Select content --</option>';
            
            items.forEach(item => {
                const option = document.createElement('option');
                option.value = item.Id;
                option.dataset.type = item.Type;
                const year = item.ProductionYear ? ` (${item.ProductionYear})` : '';
                const type = item.Type === 'Series' ? ' [TV Show]' : ' [Movie]';
                option.textContent = `${item.Name}${year}${type}`;
                select.appendChild(option);
            });
        }

        populateSeasonsDropdown(select, seasons) {
            select.innerHTML = '<option value="">-- Select season --</option>';
            
            seasons.forEach(season => {
                const option = document.createElement('option');
                option.value = season.Id;
                const seasonNum = season.IndexNumber ? ` ${season.IndexNumber}` : '';
                option.textContent = `${season.Name || 'Season' + seasonNum}`;
                select.appendChild(option);
            });
        }

        populateEpisodesDropdown(select, episodes) {
            select.innerHTML = '<option value="">-- Select episode --</option>';
            
            episodes.forEach(episode => {
                const option = document.createElement('option');
                option.value = episode.Id;
                const epNum = episode.IndexNumber ? `E${episode.IndexNumber}` : '';
                const seasonNum = episode.ParentIndexNumber ? `S${episode.ParentIndexNumber}` : '';
                option.textContent = `${seasonNum}${epNum} - ${episode.Name}`;
                select.appendChild(option);
            });
        }

        populateUserMultiSelect(select, users, selectedUserIds = []) {
            select.innerHTML = '';
            
            users.forEach(user => {
                const option = document.createElement('option');
                option.value = user.Id;
                option.textContent = user.Name;
                option.selected = selectedUserIds.includes(user.Id);
                select.appendChild(option);
            });
        }

        showSeriesControls(view) {
            view.querySelector('#seriesContainer').style.display = 'block';
            view.querySelector('#episodeContainer').style.display = 'block';
        }

        hideSeriesControls(view) {
            view.querySelector('#seriesContainer').style.display = 'none';
            view.querySelector('#episodeContainer').style.display = 'none';
            view.querySelector('#selectedSeasonId').innerHTML = '<option value="">Select a TV show first...</option>';
            view.querySelector('#selectedEpisodeId').innerHTML = '<option value="">Select a season first...</option>';
        }

        renderPartyCard(party) {
            const statusColor = party.IsActive ? '#4CAF50' : '#999';
            const statusText = party.IsActive ? 'Active' : 'Inactive';
            const waitingText = party.IsWaitingRoom ? ' (Waiting Room)' : '';
            const created = new Date(party.CreatedDate).toLocaleDateString();
            const allowedUsers = party.AllowedUserIds && party.AllowedUserIds.length > 0 ? 
                ` | Restricted (${party.AllowedUserIds.length} users)` : ' | Public';
            
            return `
                <div class="paper-card" style="padding: 1em; display: flex; justify-content: space-between; align-items: center;">
                    <div style="flex: 1;">
                        <div style="font-weight: 500; margin-bottom: 0.5em;">
                            ${party.ItemName || 'Unnamed Party'}
                            <span style="color: ${statusColor}; font-size: 0.9em; margin-left: 0.5em;">● ${statusText}${waitingText}</span>
                        </div>
                        <div style="font-size: 0.85em; color: #999;">
                            Library: ${party.CollectionName || 'Watch Party'} | 
                            Type: ${party.ItemType || 'Unknown'} | 
                            Max: ${party.MaxParticipants || 50} viewers${allowedUsers} | 
                            Created: ${created}
                        </div>
                        <div style="font-size: 0.85em; color: #666; margin-top: 0.3em;">
                            Pause: ${party.PauseControl || 'Anyone'} | 
                            Seek: ${party.HostOnlySeek ? 'Host Only' : party.LockSeekAhead ? 'No Skip Ahead' : 'Unrestricted'} | 
                            Sync: ${party.SyncToleranceSeconds || 10}s tolerance
                        </div>
                    </div>
                    <div style="display: flex; gap: 0.5em;">
                        <button is="emby-button" class="button-flat btnToggleParty" data-partyid="${party.Id}" style="padding: 0.5em 1em;">
                            <span>${party.IsActive ? 'Deactivate' : 'Activate'}</span>
                        </button>
                        <button is="emby-button" class="button-flat btnDeleteParty" data-partyid="${party.Id}" style="padding: 0.5em 1em; color: #f44336;">
                            <span>Delete</span>
                        </button>
                    </div>
                </div>
            `;
        }

        renderPartyList(container, config, deleteCallback, toggleCallback) {
            if (!config.WatchParties || config.WatchParties.length === 0) {
                container.innerHTML = '<p style="color: #999;">No active watch parties</p>';
                return;
            }

            let html = '<div style="display: flex; flex-direction: column; gap: 1em;">';
            config.WatchParties.forEach(party => {
                html += this.renderPartyCard(party);
            });
            html += '</div>';
            
            container.innerHTML = html;
            
            container.querySelectorAll('.btnDeleteParty').forEach(btn => {
                btn.addEventListener('click', (e) => {
                    const partyId = e.target.closest('button').dataset.partyid;
                    deleteCallback(partyId);
                });
            });
            
            container.querySelectorAll('.btnToggleParty').forEach(btn => {
                btn.addEventListener('click', (e) => {
                    const partyId = e.target.closest('button').dataset.partyid;
                    toggleCallback(partyId);
                });
            });
        }
    }

    return new WatchPartyUI();
});
