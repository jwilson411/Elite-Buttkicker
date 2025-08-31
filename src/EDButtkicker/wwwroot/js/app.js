// Elite Dangerous Buttkicker Configuration Interface
class ButtkickerApp {
    constructor() {
        this.init();
        this.loadDashboard();
    }

    init() {
        // Tab switching
        document.querySelectorAll('.nav-tab').forEach(tab => {
            tab.addEventListener('click', () => this.switchTab(tab.dataset.tab));
        });

        // Range input updates
        document.querySelectorAll('input[type="range"]').forEach(input => {
            input.addEventListener('input', (e) => {
                const valueSpan = e.target.nextElementSibling;
                if (valueSpan && valueSpan.classList.contains('range-value')) {
                    valueSpan.textContent = e.target.value;
                }
            });
        });

        // System status check
        this.updateSystemStatus();
        setInterval(() => this.updateSystemStatus(), 10000); // Every 10 seconds
    }

    switchTab(tabName) {
        // Update nav tabs
        document.querySelectorAll('.nav-tab').forEach(tab => {
            tab.classList.toggle('active', tab.dataset.tab === tabName);
        });

        // Update tab panels
        document.querySelectorAll('.tab-panel').forEach(panel => {
            panel.classList.toggle('active', panel.id === tabName);
        });

        // Load tab content
        switch (tabName) {
            case 'dashboard':
                this.loadDashboard();
                break;
            case 'patterns':
                this.loadPatterns();
                break;
            case 'audio':
                this.loadAudioConfig();
                break;
            case 'journal':
                this.loadJournalConfig();
                break;
            case 'context':
                this.loadContextualIntelligence();
                break;
            case 'settings':
                this.loadSettings();
                break;
        }
    }

    async updateSystemStatus() {
        try {
            const [configResponse, journalResponse] = await Promise.all([
                fetch('/api/config'),
                fetch('/api/journal/status')
            ]);

            const config = await configResponse.json();
            const journal = await journalResponse.json();

            // Update header status
            const statusElement = document.getElementById('systemStatus');
            const statusIcon = statusElement.querySelector('.status-icon');
            const statusText = statusElement.querySelector('.status-text');

            if (configResponse.ok && journalResponse.ok) {
                statusIcon.className = 'fas fa-circle status-icon online';
                statusText.textContent = journal.monitoring ? 'Connected' : 'Ready';
            } else {
                statusIcon.className = 'fas fa-circle status-icon warning';
                statusText.textContent = 'Partial Connection';
            }

            // Update dashboard status indicators
            const audioStatus = document.getElementById('audioStatus');
            const journalStatus = document.getElementById('journalStatus');
            const voiceStatus = document.getElementById('voiceStatus');
            const webStatus = document.getElementById('webStatus');

            if (audioStatus) audioStatus.className = 'status-indicator online';
            if (journalStatus) journalStatus.className = `status-indicator ${journal.monitoring ? 'online' : 'offline'}`;
            if (voiceStatus) voiceStatus.className = 'status-indicator online';
            if (webStatus) webStatus.className = 'status-indicator online';

        } catch (error) {
            console.error('Error updating system status:', error);
            const statusElement = document.getElementById('systemStatus');
            const statusIcon = statusElement.querySelector('.status-icon');
            const statusText = statusElement.querySelector('.status-text');
            
            statusIcon.className = 'fas fa-circle status-icon offline';
            statusText.textContent = 'Connection Error';
        }
    }

    async loadDashboard() {
        try {
            await this.loadRecentEvents();
            
            // Update stats
            const stats = await this.getSystemStats();
            
            const totalEventsEl = document.getElementById('totalEvents');
            const activePatternsEl = document.getElementById('activePatterns');
            const lastEventTimeEl = document.getElementById('lastEventTime');
            
            if (totalEventsEl) totalEventsEl.textContent = stats.totalEvents;
            if (activePatternsEl) activePatternsEl.textContent = stats.activePatterns;
            if (lastEventTimeEl) lastEventTimeEl.textContent = stats.lastEventTime;
            
        } catch (error) {
            console.error('Error loading dashboard:', error);
        }
    }

    async getSystemStats() {
        try {
            const [patternsResponse, eventsResponse] = await Promise.all([
                fetch('/api/patterns'),
                fetch('/api/journal/events/recent?limit=10')
            ]);

            const patterns = await patternsResponse.json();
            const events = await eventsResponse.json();

            const activePatterns = Object.values(patterns.patterns || {})
                .filter(p => p.Enabled).length;

            const totalEvents = events.events ? events.events.length : 0;
            const lastEventTime = events.events && events.events.length > 0 
                ? this.formatDateTime(events.events[0].timestamp)
                : 'Never';

            return {
                activePatterns,
                totalEvents,
                lastEventTime
            };
        } catch (error) {
            return {
                activePatterns: 0,
                totalEvents: 0,
                lastEventTime: 'Error'
            };
        }
    }

    async loadRecentEvents() {
        try {
            const response = await fetch('/api/journal/events/recent?limit=20');
            const data = await response.json();

            const eventsList = document.getElementById('recentEventsList');
            if (!eventsList) return;

            if (!data.events || data.events.length === 0) {
                eventsList.innerHTML = '<div class="loading">No recent events found</div>';
                return;
            }

            eventsList.innerHTML = data.events.map(event => `
                <div class="event-item">
                    <div class="event-info">
                        <div class="event-type">${event.event}</div>
                        <div class="event-details">
                            ${event.star_system ? `System: ${event.star_system}` : ''}
                            ${event.station_name ? ` | Station: ${event.station_name}` : ''}
                            ${event.health ? ` | Health: ${Math.round(event.health * 100)}%` : ''}
                        </div>
                    </div>
                    <div class="event-time">${this.formatDateTime(event.timestamp)}</div>
                </div>
            `).join('');

        } catch (error) {
            console.error('Error loading recent events:', error);
            const eventsList = document.getElementById('recentEventsList');
            if (eventsList) {
                eventsList.innerHTML = '<div class="loading">Error loading events</div>';
            }
        }
    }

    async loadPatterns() {
        try {
            const response = await fetch('/api/patterns');
            const data = await response.json();

            const patternsGrid = document.getElementById('patternsGrid');
            if (!patternsGrid) return;

            if (!data.patterns) {
                patternsGrid.innerHTML = '<div class="loading">No patterns found</div>';
                return;
            }

            patternsGrid.innerHTML = Object.entries(data.patterns).map(([eventType, pattern]) => `
                <div class="pattern-card">
                    <div class="pattern-header">
                        <div class="pattern-name">${pattern.Pattern.Name}</div>
                        <div class="pattern-enabled ${pattern.Enabled ? 'active' : ''}" 
                             onclick="togglePattern('${eventType}')"></div>
                    </div>
                    <div class="pattern-details">
                        <div class="pattern-detail">
                            <span class="label">Event:</span>
                            <span>${eventType}</span>
                        </div>
                        <div class="pattern-detail">
                            <span class="label">Type:</span>
                            <span>${pattern.Pattern.PatternType}</span>
                        </div>
                        <div class="pattern-detail">
                            <span class="label">Frequency:</span>
                            <span>${pattern.Pattern.Frequency} Hz</span>
                        </div>
                        <div class="pattern-detail">
                            <span class="label">Duration:</span>
                            <span>${pattern.Pattern.Duration} ms</span>
                        </div>
                        <div class="pattern-detail">
                            <span class="label">Intensity:</span>
                            <span>${pattern.Pattern.Intensity}%</span>
                        </div>
                        <div class="pattern-detail">
                            <span class="label">Curve:</span>
                            <span>${pattern.Pattern.IntensityCurve}</span>
                        </div>
                    </div>
                    <div class="pattern-actions">
                        <button class="btn btn-sm" onclick="testPattern('${eventType}')">
                            <i class="fas fa-play"></i> Test
                        </button>
                        <button class="btn btn-sm btn-secondary" onclick="editPattern('${eventType}')">
                            <i class="fas fa-edit"></i> Edit
                        </button>
                    </div>
                </div>
            `).join('');

            // Also update quick test grid if pattern tester is open
            this.updateQuickTestGrid(data.patterns);

        } catch (error) {
            console.error('Error loading patterns:', error);
            const patternsGrid = document.getElementById('patternsGrid');
            if (patternsGrid) {
                patternsGrid.innerHTML = '<div class="loading">Error loading patterns</div>';
            }
        }
    }

    async refreshPatterns() {
        try {
            // Show loading state
            const patternsGrid = document.getElementById('patternsGrid');
            if (patternsGrid) {
                patternsGrid.innerHTML = '<div class="loading"><i class="fas fa-sync-alt fa-spin"></i> Refreshing patterns...</div>';
            }

            // Call the reload endpoint first
            const reloadResponse = await fetch('/api/PatternFiles/reload', {
                method: 'POST'
            });

            if (!reloadResponse.ok) {
                throw new Error('Failed to reload pattern files');
            }

            const reloadData = await reloadResponse.json();
            
            // Show success message temporarily
            if (patternsGrid) {
                patternsGrid.innerHTML = `
                    <div class="refresh-success">
                        <i class="fas fa-check-circle"></i>
                        <div>Patterns refreshed successfully!</div>
                        <div class="refresh-stats">
                            ${reloadData.totalPacks} total packs loaded
                            ${reloadData.newPacks > 0 ? `(${reloadData.newPacks} new)` : ''}
                        </div>
                    </div>
                `;
            }

            // Wait a moment to show the success message
            await new Promise(resolve => setTimeout(resolve, 1500));

            // Then reload the patterns display
            await this.loadPatterns();

        } catch (error) {
            console.error('Error refreshing patterns:', error);
            const patternsGrid = document.getElementById('patternsGrid');
            if (patternsGrid) {
                patternsGrid.innerHTML = `
                    <div class="error-message">
                        <i class="fas fa-exclamation-triangle"></i>
                        <div>Failed to refresh patterns</div>
                        <div class="error-details">${error.message}</div>
                        <button class="btn btn-sm" onclick="app.loadPatterns()">Try Again</button>
                    </div>
                `;
            }
        }
    }

    updateQuickTestGrid(patterns) {
        const quickTestGrid = document.getElementById('quickTestGrid');
        if (!quickTestGrid || !patterns) return;

        quickTestGrid.innerHTML = Object.entries(patterns).map(([eventType, pattern]) => `
            <div class="quick-test-item">
                <div class="test-item-header">
                    <span class="test-item-name">${pattern.Pattern.Name}</span>
                    <span class="test-item-event">${eventType}</span>
                </div>
                <div class="test-item-details">
                    <span>${pattern.Pattern.Frequency}Hz</span>
                    <span>${pattern.Pattern.Duration}ms</span>
                    <span>${pattern.Pattern.Intensity}%</span>
                </div>
                <button class="btn btn-sm btn-accent" onclick="testPattern('${eventType}')">
                    <i class="fas fa-play"></i> Test
                </button>
            </div>
        `).join('');
    }

    async loadAudioConfig() {
        try {
            const response = await fetch('/api/audio/devices');
            const data = await response.json();

            const deviceList = document.getElementById('audioDeviceList');
            if (!deviceList) return;

            if (!data.devices) {
                deviceList.innerHTML = '<div class="loading">No audio devices found</div>';
                return;
            }

            deviceList.innerHTML = data.devices.map(device => `
                <div class="device-item ${device.id === data.current.id ? 'active' : ''}" 
                     onclick="selectAudioDevice(${device.id})">
                    <div class="device-info">
                        <div class="device-name">
                            ${device.name}
                            ${device.isDefault ? ' (Default)' : ''}
                        </div>
                        <div class="device-driver">${device.driver} | ${device.channels} channels</div>
                    </div>
                    ${device.isAvailable ? 
                        '<i class="fas fa-check-circle" style="color: var(--success-color);"></i>' : 
                        '<i class="fas fa-exclamation-circle" style="color: var(--warning-color);"></i>'
                    }
                </div>
            `).join('');

        } catch (error) {
            console.error('Error loading audio config:', error);
            const deviceList = document.getElementById('audioDeviceList');
            if (deviceList) {
                deviceList.innerHTML = '<div class="loading">Error loading audio devices</div>';
            }
        }
    }

    async loadJournalConfig() {
        try {
            const response = await fetch('/api/journal/status');
            const data = await response.json();

            const journalPathInput = document.getElementById('journalPath');
            const pathInfo = document.getElementById('journalPathInfo');
            const monitoringStatus = document.getElementById('monitoringStatus');

            if (journalPathInput) {
                journalPathInput.value = data.journal_path || '';
            }

            if (pathInfo) {
                pathInfo.innerHTML = `
                    <div class="info-item">
                        <span class="label">Path Status:</span>
                        <span class="value ${data.path_exists ? 'online' : ''}">${data.path_exists ? 'Valid' : 'Not Found'}</span>
                    </div>
                    <div class="info-item">
                        <span class="label">Journal Files:</span>
                        <span class="value">${data.recent_files ? data.recent_files.length : 0} found</span>
                    </div>
                    ${data.recent_files && data.recent_files.length > 0 ? `
                        <div style="margin-top: 1rem;">
                            <strong>Recent Files:</strong>
                            <ul style="margin-top: 0.5rem; padding-left: 1rem;">
                                ${data.recent_files.slice(0, 3).map(file => `<li>${file}</li>`).join('')}
                            </ul>
                        </div>
                    ` : ''}
                `;
            }

            if (monitoringStatus) {
                monitoringStatus.innerHTML = `
                    <div class="info-item">
                        <span class="label">Status:</span>
                        <span class="value ${data.monitoring ? 'online' : ''}">${data.status}</span>
                    </div>
                    <div class="info-item">
                        <span class="label">Health:</span>
                        <span class="value">${data.health}</span>
                    </div>
                    <div class="info-item">
                        <span class="label">Events Processed:</span>
                        <span class="value">${data.events_processed}</span>
                    </div>
                    <div class="info-item">
                        <span class="label">Last Event:</span>
                        <span class="value">${data.last_event_time ? this.formatDateTime(data.last_event_time) : 'Never'}</span>
                    </div>
                `;
            }
            
            // Load initial replay status and journal files
            refreshReplayStatus();
            refreshJournalFiles();

        } catch (error) {
            console.error('Error loading journal config:', error);
        }
    }

    async loadContextualIntelligence() {
        try {
            const response = await fetch('/api/context/status');
            const data = await response.json();

            // Update configuration UI
            const contextEnabled = document.getElementById('contextEnabled');
            const contextSettings = document.getElementById('contextSettings');
            const learningRate = document.getElementById('learningRate');
            const predictionThreshold = document.getElementById('predictionThreshold');
            const adaptiveIntensity = document.getElementById('adaptiveIntensity');
            const predictivePatterns = document.getElementById('predictivePatterns');
            const contextualVoice = document.getElementById('contextualVoice');
            const logAnalysis = document.getElementById('logAnalysis');

            if (contextEnabled) {
                contextEnabled.checked = data.configuration.enabled;
                if (contextSettings) {
                    contextSettings.style.display = data.configuration.enabled ? 'block' : 'none';
                }
            }

            if (learningRate) {
                learningRate.value = data.configuration.learning_rate;
                learningRate.nextElementSibling.textContent = data.configuration.learning_rate;
            }

            if (predictionThreshold) {
                predictionThreshold.value = data.configuration.prediction_threshold;
                predictionThreshold.nextElementSibling.textContent = data.configuration.prediction_threshold;
            }

            if (adaptiveIntensity) adaptiveIntensity.checked = data.configuration.adaptive_intensity;
            if (predictivePatterns) predictivePatterns.checked = data.configuration.predictive_patterns;
            if (contextualVoice) contextualVoice.checked = data.configuration.contextual_voice;
            if (logAnalysis) logAnalysis.checked = data.configuration.log_analysis;

            // Update context status
            this.updateContextStatus(data.current_context);

            // Update behavioral analysis
            this.updateBehaviorAnalysis(data.current_context, data.statistics);

            // Update predictions
            this.updatePredictions(data.predictions);

        } catch (error) {
            console.error('Error loading contextual intelligence:', error);
            const contextStatus = document.getElementById('contextStatus');
            if (contextStatus) {
                contextStatus.innerHTML = '<div class="loading">Error loading context status</div>';
            }
        }
    }

    updateContextStatus(context) {
        const contextStatus = document.getElementById('contextStatus');
        if (!contextStatus) return;

        contextStatus.innerHTML = `
            <div class="context-grid">
                <div class="context-item">
                    <span class="context-label">Game State:</span>
                    <span class="context-value ${context.game_state.toLowerCase()}">${context.game_state}</span>
                </div>
                <div class="context-item">
                    <span class="context-label">Current System:</span>
                    <span class="context-value">${context.current_system || 'Unknown'}</span>
                </div>
                <div class="context-item">
                    <span class="context-label">Threat Level:</span>
                    <span class="context-value threat-${context.threat_level.toLowerCase()}">${context.threat_level}</span>
                </div>
                <div class="context-item">
                    <span class="context-label">Hull Integrity:</span>
                    <span class="context-value ${context.hull_integrity < 0.5 ? 'warning' : ''}">${Math.round(context.hull_integrity * 100)}%</span>
                </div>
                <div class="context-item">
                    <span class="context-label">Shield Strength:</span>
                    <span class="context-value">${Math.round(context.shield_strength * 100)}%</span>
                </div>
                <div class="context-item">
                    <span class="context-label">Combat Intensity:</span>
                    <span class="context-value">${Math.round(context.combat_intensity * 100)}%</span>
                </div>
                <div class="context-item">
                    <span class="context-label">Exploration Mode:</span>
                    <span class="context-value">${context.exploration_mode}</span>
                </div>
                <div class="context-item">
                    <span class="context-label">Intensity Multiplier:</span>
                    <span class="context-value">${context.intensity_multiplier.toFixed(2)}x</span>
                </div>
            </div>
        `;
    }

    updateBehaviorAnalysis(context, statistics) {
        const behaviorAnalysis = document.getElementById('behaviorAnalysis');
        if (!behaviorAnalysis) return;

        behaviorAnalysis.innerHTML = `
            <div class="behavior-grid">
                <div class="behavior-section">
                    <h4>Player Traits</h4>
                    <div class="trait-item">
                        <span class="trait-label">Aggressiveness:</span>
                        <div class="trait-bar">
                            <div class="trait-fill" style="width: ${context.player_aggressiveness * 100}%"></div>
                        </div>
                        <span class="trait-value">${Math.round(context.player_aggressiveness * 100)}%</span>
                    </div>
                    <div class="trait-item">
                        <span class="trait-label">Cautiousness:</span>
                        <div class="trait-bar">
                            <div class="trait-fill cautious" style="width: ${context.player_cautiousness * 100}%"></div>
                        </div>
                        <span class="trait-value">${Math.round(context.player_cautiousness * 100)}%</span>
                    </div>
                </div>
                
                <div class="behavior-section">
                    <h4>Activity Statistics</h4>
                    <div class="stat-item">
                        <span class="stat-label">Systems Visited:</span>
                        <span class="stat-value">${statistics.systems_visited}</span>
                    </div>
                    <div class="stat-item">
                        <span class="stat-label">Bodies Scanned:</span>
                        <span class="stat-value">${statistics.bodies_scanned}</span>
                    </div>
                    <div class="stat-item">
                        <span class="stat-label">Recent Events:</span>
                        <span class="stat-value">${statistics.recent_event_types.join(', ')}</span>
                    </div>
                </div>

                <div class="behavior-section">
                    <h4>Time Distribution</h4>
                    <div class="time-distribution">
                        ${statistics.state_time_spent.map(state => `
                            <div class="time-item">
                                <span class="time-label">${state.state}:</span>
                                <span class="time-value">${Math.round(state.time_minutes)} min</span>
                            </div>
                        `).join('')}
                    </div>
                </div>
            </div>
        `;
    }

    updatePredictions(predictions) {
        const predictionsDiv = document.getElementById('predictions');
        if (!predictionsDiv) return;

        predictionsDiv.innerHTML = `
            <div class="predictions-grid">
                <div class="prediction-section">
                    <h4>Next State Prediction</h4>
                    <div class="prediction-item">
                        <span class="prediction-label">Predicted State:</span>
                        <span class="prediction-value">${predictions.predicted_next_state || 'None'}</span>
                    </div>
                    <div class="prediction-item">
                        <span class="prediction-label">Confidence:</span>
                        <span class="prediction-value">${Math.round(predictions.prediction_confidence * 100)}%</span>
                    </div>
                </div>

                <div class="prediction-section">
                    <h4>Likely Upcoming Events</h4>
                    <div class="events-list">
                        ${predictions.likely_upcoming_events && predictions.likely_upcoming_events.length > 0 
                            ? predictions.likely_upcoming_events.map(event => `
                                <div class="event-prediction">${event}</div>
                            `).join('')
                            : '<div class="no-predictions">No predictions available</div>'
                        }
                    </div>
                </div>
            </div>
        `;
    }

    async loadSettings() {
        try {
            const response = await fetch('/api/config');
            const data = await response.json();
            
            // Settings are already populated in the HTML
            console.log('Configuration loaded:', data);
            
        } catch (error) {
            console.error('Error loading settings:', error);
        }
    }

    // Utility methods
    formatDateTime(timestamp) {
        const date = new Date(timestamp);
        return date.toLocaleString();
    }

    showToast(message, type = 'success') {
        const toastContainer = document.getElementById('toastContainer');
        const toast = document.createElement('div');
        toast.className = `toast ${type}`;
        toast.innerHTML = `
            <div style="display: flex; align-items: center; gap: 0.5rem;">
                <i class="fas ${type === 'success' ? 'fa-check-circle' : type === 'error' ? 'fa-exclamation-circle' : 'fa-info-circle'}"></i>
                ${message}
            </div>
        `;

        toastContainer.appendChild(toast);

        // Show toast
        setTimeout(() => toast.classList.add('show'), 100);

        // Remove toast
        setTimeout(() => {
            toast.classList.remove('show');
            setTimeout(() => toastContainer.removeChild(toast), 300);
        }, 4000);
    }
}

// Global functions for inline event handlers
window.refreshDashboard = () => app.loadDashboard();
window.loadRecentEvents = () => app.loadRecentEvents();

window.togglePattern = async (eventType) => {
    try {
        // This would need to be implemented in the API
        app.showToast(`Pattern ${eventType} toggled`, 'success');
    } catch (error) {
        app.showToast('Error toggling pattern', 'error');
    }
};

window.testPattern = async (eventType) => {
    try {
        const response = await fetch(`/api/patterns/${eventType}/test`, {
            method: 'POST'
        });
        
        if (response.ok) {
            const result = await response.json();
            app.showToast(`Pattern "${eventType}" tested successfully!`, 'success');
        } else {
            app.showToast('Error testing pattern', 'error');
        }
    } catch (error) {
        console.error('Error testing pattern:', error);
        app.showToast('Error testing pattern', 'error');
    }
};

window.editPattern = (eventType) => {
    // TODO: Implement pattern editor modal
    app.showToast(`Pattern editor for ${eventType} - Coming soon!`, 'warning');
};

window.createNewPattern = () => {
    // TODO: Implement new pattern creation
    app.showToast('New pattern creation - Coming soon!', 'warning');
};

window.selectAudioDevice = async (deviceId) => {
    try {
        const response = await fetch('/api/audio/device', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ deviceId: deviceId })
        });

        if (response.ok) {
            app.showToast('Audio device updated successfully!', 'success');
            app.loadAudioConfig(); // Refresh the device list
        } else {
            app.showToast('Error setting audio device', 'error');
        }
    } catch (error) {
        console.error('Error setting audio device:', error);
        app.showToast('Error setting audio device', 'error');
    }
};

window.testAudio = async () => {
    try {
        const response = await fetch('/api/audio/test', {
            method: 'POST'
        });

        if (response.ok) {
            app.showToast('Audio test completed!', 'success');
        } else {
            app.showToast('Error testing audio', 'error');
        }
    } catch (error) {
        console.error('Error testing audio:', error);
        app.showToast('Error testing audio', 'error');
    }
};

window.setJournalPath = async () => {
    const pathInput = document.getElementById('journalPath');
    const path = pathInput.value.trim();

    if (!path) {
        app.showToast('Please enter a journal path', 'warning');
        return;
    }

    try {
        const response = await fetch('/api/journal/path', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ path: path })
        });

        if (response.ok) {
            app.showToast('Journal path updated successfully!', 'success');
            app.loadJournalConfig(); // Refresh the status
        } else {
            const error = await response.json();
            app.showToast(error.error || 'Error setting journal path', 'error');
        }
    } catch (error) {
        console.error('Error setting journal path:', error);
        app.showToast('Error setting journal path', 'error');
    }
};

window.refreshJournalStatus = () => app.loadJournalConfig();

window.exportConfiguration = async () => {
    try {
        const response = await fetch('/api/config/export');
        
        if (response.ok) {
            const blob = await response.blob();
            const url = window.URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = `ed-buttkicker-config-${new Date().toISOString().slice(0, 19).replace(/:/g, '-')}.json`;
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            window.URL.revokeObjectURL(url);
            
            app.showToast('Configuration exported successfully!', 'success');
        } else {
            app.showToast('Error exporting configuration', 'error');
        }
    } catch (error) {
        console.error('Error exporting configuration:', error);
        app.showToast('Error exporting configuration', 'error');
    }
};

window.importConfiguration = () => {
    const input = document.createElement('input');
    input.type = 'file';
    input.accept = '.json';
    input.onchange = async (e) => {
        const file = e.target.files[0];
        if (!file) return;

        try {
            const text = await file.text();
            const response = await fetch('/api/config/import', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: text
            });

            if (response.ok) {
                app.showToast('Configuration imported successfully!', 'success');
                app.loadSettings(); // Refresh settings
            } else {
                const error = await response.json();
                app.showToast(error.error || 'Error importing configuration', 'error');
            }
        } catch (error) {
            console.error('Error importing configuration:', error);
            app.showToast('Error importing configuration', 'error');
        }
    };
    
    input.click();
};

// Pattern editor modal functions
window.closePatternModal = () => {
    document.getElementById('patternModal').classList.remove('active');
};

window.savePattern = () => {
    app.showToast('Pattern saved - Coming soon!', 'warning');
    closePatternModal();
};

window.testCurrentPattern = () => {
    app.showToast('Testing current pattern - Coming soon!', 'warning');
};

window.refreshPatterns = () => {
    if (app) {
        app.refreshPatterns();
    }
};

// Contextual Intelligence Functions
window.refreshContextStatus = () => {
    if (app) {
        app.loadContextualIntelligence();
    }
};

window.toggleContextualIntelligence = async () => {
    const checkbox = document.getElementById('contextEnabled');
    const settings = document.getElementById('contextSettings');
    
    if (settings) {
        settings.style.display = checkbox.checked ? 'block' : 'none';
    }
    
    // Save the enabled state immediately
    await saveContextualIntelligenceEnabled(checkbox.checked);
};

window.saveContextConfiguration = async () => {
    try {
        const enabled = document.getElementById('contextEnabled').checked;
        const learningRate = parseFloat(document.getElementById('learningRate').value);
        const predictionThreshold = parseFloat(document.getElementById('predictionThreshold').value);
        const adaptiveIntensity = document.getElementById('adaptiveIntensity').checked;
        const predictivePatterns = document.getElementById('predictivePatterns').checked;
        const contextualVoice = document.getElementById('contextualVoice').checked;
        const logAnalysis = document.getElementById('logAnalysis').checked;

        const config = {
            enabled,
            learning_rate: learningRate,
            prediction_threshold: predictionThreshold,
            adaptive_intensity: adaptiveIntensity,
            predictive_patterns: predictivePatterns,
            contextual_voice: contextualVoice,
            log_analysis: logAnalysis
        };

        const response = await fetch('/api/context/config', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(config)
        });

        if (response.ok) {
            const result = await response.json();
            app.showToast('Contextual Intelligence configuration saved successfully!', 'success');
            app.loadContextualIntelligence(); // Refresh the display
        } else {
            const error = await response.json();
            app.showToast(error.error || 'Error saving configuration', 'error');
        }
    } catch (error) {
        console.error('Error saving contextual intelligence configuration:', error);
        app.showToast('Error saving configuration', 'error');
    }
};

async function saveContextualIntelligenceEnabled(enabled) {
    try {
        const config = { enabled };

        const response = await fetch('/api/context/config', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(config)
        });

        if (response.ok) {
            app.showToast(`Contextual Intelligence ${enabled ? 'enabled' : 'disabled'}!`, 'success');
        } else {
            const error = await response.json();
            app.showToast(error.error || 'Error updating configuration', 'error');
        }
    } catch (error) {
        console.error('Error updating contextual intelligence status:', error);
        app.showToast('Error updating configuration', 'error');
    }
}

// Pattern Tester Functions
window.showPatternTester = () => {
    const patternTester = document.getElementById('patternTester');
    const patternsGrid = document.getElementById('patternsGrid');
    
    if (patternTester && patternsGrid) {
        patternTester.style.display = 'block';
        patternsGrid.style.display = 'none';
        
        // Load patterns for quick testing
        app.loadPatterns();
    }
};

window.hidePatternTester = () => {
    const patternTester = document.getElementById('patternTester');
    const patternsGrid = document.getElementById('patternsGrid');
    
    if (patternTester && patternsGrid) {
        patternTester.style.display = 'none';
        patternsGrid.style.display = 'grid';
    }
};

window.updateRangeDisplay = (input) => {
    const valueSpan = input.nextElementSibling;
    if (valueSpan && valueSpan.classList.contains('range-value')) {
        valueSpan.textContent = input.value;
    }
};

window.updatePatternTypeOptions = () => {
    const patternType = document.getElementById('testPatternType').value;
    const multiLayerControls = document.getElementById('multiLayerControls');
    
    if (multiLayerControls) {
        multiLayerControls.style.display = patternType === 'MultiLayer' ? 'block' : 'none';
        
        if (patternType === 'MultiLayer') {
            // Initialize with one layer if none exist
            const layerControls = document.getElementById('layerControls');
            if (layerControls && layerControls.children.length === 0) {
                addLayer();
            }
        }
    }
};

window.addLayer = () => {
    const layerControls = document.getElementById('layerControls');
    if (!layerControls) return;
    
    const layerIndex = layerControls.children.length;
    const layerDiv = document.createElement('div');
    layerDiv.className = 'layer-control';
    layerDiv.innerHTML = `
        <div class="layer-header">
            <h6>Layer ${layerIndex + 1}</h6>
            <button type="button" class="btn-remove" onclick="removeLayer(this)">&times;</button>
        </div>
        <div class="layer-params">
            <div class="form-group">
                <label>Waveform</label>
                <select class="layer-waveform">
                    <option value="Sine">Sine</option>
                    <option value="Square">Square</option>
                    <option value="Triangle">Triangle</option>
                    <option value="Sawtooth">Sawtooth</option>
                    <option value="Noise">Noise</option>
                </select>
            </div>
            <div class="form-group">
                <label>Frequency (Hz)</label>
                <input type="range" class="layer-frequency" min="20" max="80" value="40" oninput="updateRangeDisplay(this)">
                <span class="range-value">40</span>
            </div>
            <div class="form-group">
                <label>Amplitude</label>
                <input type="range" class="layer-amplitude" min="0.1" max="1.0" step="0.1" value="0.8" oninput="updateRangeDisplay(this)">
                <span class="range-value">0.8</span>
            </div>
        </div>
    `;
    
    layerControls.appendChild(layerDiv);
};

window.removeLayer = (button) => {
    const layerControl = button.closest('.layer-control');
    if (layerControl) {
        layerControl.remove();
        
        // Update layer numbers
        const layerControls = document.getElementById('layerControls');
        const layers = layerControls.querySelectorAll('.layer-control');
        layers.forEach((layer, index) => {
            const header = layer.querySelector('.layer-header h6');
            if (header) {
                header.textContent = `Layer ${index + 1}`;
            }
        });
    }
};

window.testCustomPattern = async () => {
    try {
        const patternParams = {
            patternType: document.getElementById('testPatternType').value,
            frequency: parseFloat(document.getElementById('testFrequency').value),
            duration: parseInt(document.getElementById('testDuration').value),
            intensity: parseInt(document.getElementById('testIntensity').value),
            fadeIn: parseInt(document.getElementById('testFadeIn').value),
            fadeOut: parseInt(document.getElementById('testFadeOut').value),
            intensityCurve: document.getElementById('testIntensityCurve').value
        };

        // Handle multi-layer patterns
        if (patternParams.patternType === 'MultiLayer') {
            const layerControls = document.getElementById('layerControls');
            if (layerControls) {
                const layers = [];
                layerControls.querySelectorAll('.layer-control').forEach(layerDiv => {
                    const waveform = layerDiv.querySelector('.layer-waveform').value;
                    const frequency = parseFloat(layerDiv.querySelector('.layer-frequency').value);
                    const amplitude = parseFloat(layerDiv.querySelector('.layer-amplitude').value);
                    
                    layers.push({
                        waveform,
                        frequency,
                        amplitude,
                        curve: "Linear",
                        phaseOffset: 0
                    });
                });
                patternParams.layers = layers;
            }
        }

        const response = await fetch('/api/patterns/test/custom', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(patternParams)
        });

        if (response.ok) {
            const result = await response.json();
            app.showToast(`Custom pattern tested successfully! (${result.pattern.Duration}ms at ${result.pattern.Frequency}Hz)`, 'success');
        } else {
            const error = await response.json();
            app.showToast(error.error || 'Error testing custom pattern', 'error');
        }
    } catch (error) {
        console.error('Error testing custom pattern:', error);
        app.showToast('Error testing custom pattern', 'error');
    }
};

window.resetPatternTester = () => {
    // Reset all form controls to default values
    document.getElementById('testPatternType').value = 'SharpPulse';
    document.getElementById('testFrequency').value = 40;
    document.getElementById('testDuration').value = 1000;
    document.getElementById('testIntensity').value = 80;
    document.getElementById('testFadeIn').value = 50;
    document.getElementById('testFadeOut').value = 50;
    document.getElementById('testIntensityCurve').value = 'Linear';
    
    // Update range displays
    document.querySelectorAll('#patternTester input[type="range"]').forEach(input => {
        updateRangeDisplay(input);
    });
    
    // Clear multi-layer controls
    const layerControls = document.getElementById('layerControls');
    if (layerControls) {
        layerControls.innerHTML = '';
    }
    
    // Hide multi-layer controls
    updatePatternTypeOptions();
    
    app.showToast('Pattern tester reset to defaults', 'info');
};

// Journal Replay Functions
window.startJournalReplay = async () => {
    try {
        const selectedJournalFile = document.getElementById('journalFileSelect')?.value;
        
        const requestBody = {};
        if (selectedJournalFile && selectedJournalFile !== '') {
            requestBody.journalFile = selectedJournalFile;
        }

        const response = await fetch('/api/journal/replay/start', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(requestBody)
        });

        if (response.ok) {
            const result = await response.json();
            app.showToast(`Journal replay started with ${result.events_count} events from ${result.source}!`, 'success');
            updateReplayUI(true);
            
            // Update source display
            const replaySource = document.getElementById('replaySource');
            if (replaySource) {
                replaySource.textContent = result.source || 'recent_events';
            }
            
            // Update status every 2 seconds while replaying
            startReplayStatusUpdates();
        } else {
            const error = await response.json();
            app.showToast(error.error || 'Error starting journal replay', 'error');
        }
    } catch (error) {
        console.error('Error starting journal replay:', error);
        app.showToast('Error starting journal replay', 'error');
    }
};

window.stopJournalReplay = async () => {
    try {
        const response = await fetch('/api/journal/replay/stop', {
            method: 'POST'
        });

        if (response.ok) {
            app.showToast('Journal replay stopped', 'info');
            updateReplayUI(false);
            stopReplayStatusUpdates();
        } else {
            const error = await response.json();
            app.showToast(error.error || 'Error stopping journal replay', 'error');
        }
    } catch (error) {
        console.error('Error stopping journal replay:', error);
        app.showToast('Error stopping journal replay', 'error');
    }
};

window.refreshReplayStatus = async () => {
    try {
        const response = await fetch('/api/journal/replay/status');
        
        if (response.ok) {
            const status = await response.json();
            
            // Update UI elements
            const replayStatusText = document.getElementById('replayStatusText');
            const replayEventCount = document.getElementById('replayEventCount');
            const replayIndicator = document.getElementById('replayIndicator');
            
            if (replayStatusText) {
                replayStatusText.textContent = status.is_replaying ? 'Running' : 'Stopped';
            }
            
            if (replayEventCount) {
                replayEventCount.textContent = status.last_5_minutes_events || 0;
            }
            
            if (replayIndicator) {
                replayIndicator.className = `status-indicator ${status.is_replaying ? 'online' : 'offline'}`;
            }
            
            updateReplayUI(status.is_replaying);
            
            // If replay stopped naturally, stop status updates
            if (!status.is_replaying) {
                stopReplayStatusUpdates();
            }
        }
    } catch (error) {
        console.error('Error refreshing replay status:', error);
    }
};

function updateReplayUI(isReplaying) {
    const startBtn = document.getElementById('startReplayBtn');
    const stopBtn = document.getElementById('stopReplayBtn');
    
    if (startBtn) {
        startBtn.disabled = isReplaying;
        startBtn.innerHTML = isReplaying ? '<i class="fas fa-play"></i> Running...' : '<i class="fas fa-play"></i> Start Replay';
    }
    
    if (stopBtn) {
        stopBtn.disabled = !isReplaying;
    }
}

let replayStatusInterval;

function startReplayStatusUpdates() {
    stopReplayStatusUpdates(); // Clear any existing interval
    replayStatusInterval = setInterval(refreshReplayStatus, 2000); // Every 2 seconds
}

function stopReplayStatusUpdates() {
    if (replayStatusInterval) {
        clearInterval(replayStatusInterval);
        replayStatusInterval = null;
    }
}

// Journal File Management Functions
window.refreshJournalFiles = async () => {
    try {
        const response = await fetch('/api/journal/status');
        
        if (response.ok) {
            const status = await response.json();
            const journalFileSelect = document.getElementById('journalFileSelect');
            
            if (journalFileSelect && status.recent_files) {
                // Clear existing options
                journalFileSelect.innerHTML = '';
                
                // Add default option
                const defaultOption = document.createElement('option');
                defaultOption.value = '';
                defaultOption.textContent = 'Use recent events from memory';
                journalFileSelect.appendChild(defaultOption);
                
                // Add journal files (most recent first)
                status.recent_files.forEach(fileName => {
                    const option = document.createElement('option');
                    option.value = fileName;
                    option.textContent = fileName;
                    journalFileSelect.appendChild(option);
                });
                
                if (status.recent_files.length === 0) {
                    const noFilesOption = document.createElement('option');
                    noFilesOption.value = '';
                    noFilesOption.textContent = 'No journal files found';
                    noFilesOption.disabled = true;
                    journalFileSelect.appendChild(noFilesOption);
                }
            }
        }
    } catch (error) {
        console.error('Error refreshing journal files:', error);
        const journalFileSelect = document.getElementById('journalFileSelect');
        if (journalFileSelect) {
            journalFileSelect.innerHTML = '<option value="">Error loading journal files</option>';
        }
    }
};

// Initialize app when DOM is loaded
let app;
document.addEventListener('DOMContentLoaded', () => {
    app = new ButtkickerApp();
});