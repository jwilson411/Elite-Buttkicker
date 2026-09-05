// Elite Dangerous Buttkicker Configuration Interface
class ButtkickerApp {
    constructor() {
        this.setup = null;
        this.init();
        this.loadDashboard();
        this.loadSetupState();
    }

    init() {
        // Tab switching. The nav is a tablist: clicking a tab still selects it, and so do the
        // arrow keys, Home and End, with only the selected tab in the tab order.
        const tabs = Array.from(document.querySelectorAll('.nav-tab'));
        tabs.forEach(tab => {
            tab.addEventListener('click', () => this.switchTab(tab.dataset.tab));
            tab.addEventListener('keydown', event => this.onTabKeydown(event, tabs));
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

    // Enter and Space are the button's own activation and already reach the click handler, so only
    // the roving-focus keys are handled here.
    onTabKeydown(event, tabs) {
        const current = tabs.indexOf(event.currentTarget);
        if (current < 0) return;

        let next;
        switch (event.key) {
            case 'ArrowRight': next = (current + 1) % tabs.length; break;
            case 'ArrowLeft': next = (current - 1 + tabs.length) % tabs.length; break;
            case 'Home': next = 0; break;
            case 'End': next = tabs.length - 1; break;
            default: return;
        }

        event.preventDefault();
        this.switchTab(tabs[next].dataset.tab);
        tabs[next].focus();
    }

    switchTab(tabName) {
        // Update nav tabs
        document.querySelectorAll('.nav-tab').forEach(tab => {
            const selected = tab.dataset.tab === tabName;
            tab.classList.toggle('active', selected);
            tab.setAttribute('aria-selected', selected ? 'true' : 'false');
            tab.tabIndex = selected ? 0 : -1;
        });

        // Update tab panels. The unselected ones are hidden outright, so nothing in them is
        // reachable by a screen reader or by Tab.
        document.querySelectorAll('.tab-panel').forEach(panel => {
            const selected = panel.id === tabName;
            panel.classList.toggle('active', selected);
            panel.hidden = !selected;
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

    // Health is read from the subsystems themselves - never inferred from an unrelated request
    // returning 200 - so every row can say what state it is in and why.
    async updateSystemStatus() {
        try {
            const response = await fetch('/api/health');
            if (!response.ok) throw new Error(`/api/health returned ${response.status}`);

            this.renderHealth(await response.json());
        } catch (error) {
            console.error('Error updating system status:', error);

            this.setHeaderStatus('offline', 'Connection Error');

            const list = document.getElementById('systemHealthList');
            if (list) {
                dom.replace(list, dom.el('div', {
                    className: 'loading',
                    text: 'Could not read system health from the local service.'
                }));
            }
        }
    }

    renderHealth(report) {
        this.setHeaderStatus(
            ButtkickerApp.statusIconClass(report.status),
            ButtkickerApp.statusLabel(report.status));

        const list = document.getElementById('systemHealthList');
        if (!list) return;

        const { el, replace, icon } = dom;

        replace(list, (report.components || []).map(component => el('div', { className: 'health-item' }, [
            el('div', { className: 'health-summary' }, [
                el('span', { className: `status-indicator ${ButtkickerApp.statusDotClass(component.status)}` }),
                el('div', { className: 'health-text' }, [
                    el('div', { className: 'health-name', text: component.name }),
                    el('div', { className: 'health-reason', text: component.reason || '' }),
                    component.detail ? el('div', { className: 'health-detail', text: component.detail }) : null
                ])
            ]),
            component.retry
                ? el('button', {
                    className: 'btn btn-sm',
                    on: { click: () => retryHealthComponent(component.id) }
                }, [icon('fa-redo'), ' ', component.retry.label])
                : null
        ])));
    }

    setHeaderStatus(iconClass, text) {
        const statusElement = document.getElementById('systemStatus');
        if (!statusElement) return;

        const statusIcon = statusElement.querySelector('.status-icon');
        const statusText = statusElement.querySelector('.status-text');

        if (statusIcon) statusIcon.className = `fas fa-circle status-icon ${iconClass}`;
        if (statusText) statusText.textContent = text;
    }

    static statusDotClass(status) {
        switch (status) {
            case 'ok': return 'online';
            case 'error': return 'offline';
            case 'off': return 'muted';
            default: return 'warning';
        }
    }

    static statusIconClass(status) {
        switch (status) {
            case 'ok': return 'online';
            case 'error': return 'offline';
            default: return 'warning';
        }
    }

    static statusLabel(status) {
        switch (status) {
            case 'ok': return 'All systems ready';
            case 'error': return 'Needs attention';
            case 'pending': return 'Setup incomplete';
            case 'attention': return 'Needs attention';
            default: return 'Checking...';
        }
    }

    // ----- First-run setup wizard -----

    async loadSetupState(forceOpen = false) {
        try {
            const response = await fetch('/api/setup/status');
            if (!response.ok) throw new Error(`/api/setup/status returned ${response.status}`);

            this.setup = await response.json();

            if (this.setup.show_wizard || forceOpen) {
                this.openSetupWizard();
            }

            if (this.setup.health) {
                this.renderHealth(this.setup.health);
            }
        } catch (error) {
            console.error('Error loading setup state:', error);
        }
    }

    applySetupStatus(status) {
        if (!status) return;

        this.setup = status;
        this.renderSetup();

        if (status.health) {
            this.renderHealth(status.health);
        }
    }

    openSetupWizard(stepId = null) {
        const modal = document.getElementById('setupWizard');
        if (!modal) return;

        this.activeStep = stepId || (this.setup ? this.setup.current_step : 'journal');
        // Render first so the step's own controls are what focus lands on.
        this.renderSetup();
        openDialog('setupWizard');
    }

    closeSetupWizard() {
        closeDialog('setupWizard');
    }

    renderSetup() {
        const stepsList = document.getElementById('setupSteps');
        const panel = document.getElementById('setupPanel');
        const note = document.getElementById('setupNote');
        if (!stepsList || !panel || !this.setup) return;

        const steps = this.setup.steps || [];
        if (!steps.some(step => step.id === this.activeStep)) {
            this.activeStep = this.setup.current_step;
        }

        const { el, replace, icon } = dom;

        replace(stepsList, steps.map(step => el('li', {
            className: [
                'setup-step',
                step.complete ? 'complete' : '',
                step.id === this.activeStep ? 'active' : ''
            ].filter(Boolean).join(' '),
            on: { click: () => showSetupStep(step.id) }
        }, [
            icon(step.complete ? 'fa-check-circle' : 'fa-circle-notch'),
            el('div', {}, [
                el('div', { className: 'setup-step-title', text: step.title }),
                el('div', { className: 'setup-step-summary', text: step.summary || '' })
            ])
        ])));

        if (note) {
            note.textContent = this.setup.completed
                ? `Setup was completed${this.setup.completed_at ? ' on ' + this.formatDateTime(this.setup.completed_at) : ''}. Reopening it changes nothing until you confirm a step.`
                : 'Nothing is saved until you confirm each step.';
        }

        switch (this.activeStep) {
            case 'journal':
                this.renderJournalStep(panel);
                break;
            case 'audio-device':
                this.renderAudioDeviceStep(panel);
                break;
            case 'audio-test':
                this.renderAudioTestStep(panel);
                break;
            default:
                this.renderFinishStep(panel);
                break;
        }
    }

    async renderJournalStep(panel) {
        const { el, replace } = dom;

        replace(panel, el('div', { className: 'loading', text: 'Looking for your Elite Dangerous journal folder...' }));

        try {
            const response = await fetch('/api/setup/journal/candidates');
            const data = await response.json();
            const candidates = data.candidates || [];

            replace(panel, [
                el('h4', { text: '1. Find your Elite Dangerous journal' }),
                el('p', { className: 'setup-help' }, [
                    'Elite Dangerous writes a ',
                    el('code', { text: 'Journal.*.log' }),
                    ' file every session. These are the folders found on this machine.'
                ]),
                el('div', { className: 'setup-candidates' }, [
                    candidates.length === 0
                        ? el('div', { className: 'loading', text: 'No candidate folders found - enter the path below.' })
                        : null,
                    ...candidates.map(candidate => el('div', {
                        className: `setup-candidate ${candidate.is_configured ? 'active' : ''}`.trim()
                    }, [
                        el('div', {}, [
                            el('div', { className: 'setup-candidate-path', text: candidate.path }),
                            el('div', { className: 'setup-candidate-detail' }, [
                                candidate.exists
                                    ? `${dom.num(candidate.journal_files_found)} journal file(s)`
                                    : 'Folder does not exist',
                                candidate.is_recommended ? ' · recommended' : ''
                            ])
                        ]),
                        el('button', {
                            className: 'btn btn-sm btn-primary',
                            disabled: !candidate.exists,
                            text: 'Use this folder',
                            on: { click: () => confirmJournalPath(candidate.path) }
                        })
                    ]))
                ]),
                el('div', { className: 'form-group' }, [
                    el('label', { attrs: { for: 'setupJournalPath' }, text: 'Or enter the folder yourself' }),
                    el('input', { id: 'setupJournalPath', attrs: { type: 'text' }, value: data.configured_path || '' })
                ]),
                el('button', {
                    className: 'btn btn-primary',
                    text: 'Confirm journal folder',
                    on: { click: () => confirmJournalPath() }
                })
            ]);
        } catch (error) {
            console.error('Error loading journal candidates:', error);
            replace(panel, el('div', { className: 'loading', text: 'Could not search for journal folders.' }));
        }
    }

    async renderAudioDeviceStep(panel) {
        const { el, replace } = dom;

        replace(panel, el('div', { className: 'loading', text: 'Reading output devices...' }));

        try {
            const response = await fetch('/api/audio/devices');
            const data = await response.json();
            const devices = data.devices || [];

            replace(panel, [
                el('h4', { text: '2. Choose your output device' }),
                el('p', {
                    className: 'setup-help',
                    text: 'Pick the device your buttkicker amplifier is connected to. The device endpoint id '
                        + 'is saved, so the choice survives devices being plugged in or removed.'
                }),
                el('div', { className: 'setup-candidates' }, [
                    devices.length === 0
                        ? el('div', { className: 'loading', text: 'No output devices reported.' })
                        : null,
                    ...devices.map(device => el('div', {
                        className: `setup-candidate ${isSelectedAudioDevice(device, data.current) ? 'active' : ''}`.trim()
                    }, [
                        el('div', {}, [
                            el('div', { className: 'setup-candidate-path', text: device.name }),
                            el('div', { className: 'setup-candidate-detail' }, [
                                device.driver,
                                device.isDefault ? ' · system default' : '',
                                device.isAvailable ? '' : ' · not active'
                            ])
                        ]),
                        el('button', {
                            className: 'btn btn-sm btn-primary',
                            disabled: !device.isAvailable,
                            text: 'Use this device',
                            on: { click: () => selectSetupAudioDevice(device.endpointId || '', Number(device.id)) }
                        })
                    ]))
                ])
            ]);
        } catch (error) {
            console.error('Error loading audio devices:', error);
            replace(panel, el('div', { className: 'loading', text: 'Could not read the output devices.' }));
        }
    }

    renderAudioTestStep(panel) {
        const { el, replace, icon } = dom;
        const step = (this.setup.steps || []).find(s => s.id === 'audio-test');

        replace(panel, [
            el('h4', { text: '3. Run a quiet test' }),
            el('p', {
                className: 'setup-help',
                text: 'This plays a short, deliberately quiet low-frequency tone so you can set your '
                    + 'amplifier gain from silence upwards rather than being surprised at full intensity.'
            }),
            el('div', {
                className: 'setup-result',
                id: 'setupTestResult',
                text: step && step.complete ? step.summary : 'No test has been run yet.'
            }),
            el('button', {
                className: 'btn btn-primary',
                on: { click: () => runSetupAudioTest() }
            }, [icon('fa-volume-down'), ' Play test tone'])
        ]);
    }

    renderFinishStep(panel) {
        const { el, replace, icon } = dom;
        const incomplete = this.setup.incomplete_steps || [];
        const remaining = incomplete.filter(id => id !== 'finish');

        replace(panel, [
            el('h4', { text: '4. Finish setup' }),
            remaining.length > 0
                ? el('p', { className: 'setup-help' }, [
                    `These steps have not been confirmed yet: ${remaining.join(', ')}. `,
                    'You can still finish - the dashboard will keep showing what is missing.'
                ])
                : el('p', { className: 'setup-help', text: 'Every step has been confirmed.' }),
            el('button', {
                className: 'btn btn-primary',
                on: { click: () => completeSetup() }
            }, [icon('fa-check'), ' ', this.setup.completed ? 'Save and close' : 'Finish setup'])
        ]);
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

            const { el, replace } = dom;

            if (!data.events || data.events.length === 0) {
                replace(eventsList, el('div', { className: 'loading', text: 'No recent events found' }));
                return;
            }

            replace(eventsList, data.events.map(event => el('div', { className: 'event-item' }, [
                el('div', { className: 'event-info' }, [
                    el('div', { className: 'event-type', text: event.event }),
                    el('div', { className: 'event-details' }, [
                        event.star_system ? `System: ${event.star_system}` : '',
                        event.station_name ? ` | Station: ${event.station_name}` : '',
                        event.health ? ` | Health: ${Math.round(dom.num(event.health) * 100)}%` : ''
                    ])
                ]),
                el('div', { className: 'event-time', text: this.formatDateTime(event.timestamp) })
            ])));

        } catch (error) {
            console.error('Error loading recent events:', error);
            const eventsList = document.getElementById('recentEventsList');
            if (eventsList) {
                dom.replace(eventsList, dom.el('div', { className: 'loading', text: 'Error loading events' }));
            }
        }
    }

    async loadPatterns() {
        try {
            const response = await fetch('/api/patterns');
            const data = await response.json();

            const patternsGrid = document.getElementById('patternsGrid');
            if (!patternsGrid) return;

            const { el, replace, icon } = dom;

            if (!data.patterns) {
                replace(patternsGrid, el('div', { className: 'loading', text: 'No patterns found' }));
                return;
            }

            const detail = (label, value) => el('div', { className: 'pattern-detail' }, [
                el('span', { className: 'label', text: label }),
                el('span', { text: value })
            ]);

            replace(patternsGrid, Object.entries(data.patterns).map(([eventType, pattern]) => el('div', { className: 'pattern-card' }, [
                el('div', { className: 'pattern-header' }, [
                    el('div', { className: 'pattern-name', text: pattern.Pattern.Name }),
                    el('div', {
                        className: `pattern-enabled ${pattern.Enabled ? 'active' : ''}`.trim(),
                        on: { click: () => togglePattern(eventType) }
                    })
                ]),
                el('div', { className: 'pattern-details' }, [
                    detail('Event:', eventType),
                    detail('Type:', pattern.Pattern.PatternType),
                    detail('Frequency:', `${pattern.Pattern.Frequency} Hz`),
                    detail('Duration:', `${pattern.Pattern.Duration} ms`),
                    detail('Intensity:', `${pattern.Pattern.Intensity}%`),
                    detail('Curve:', pattern.Pattern.IntensityCurve)
                ]),
                el('div', { className: 'pattern-actions' }, [
                    el('button', {
                        className: 'btn btn-sm',
                        on: { click: () => testPattern(eventType) }
                    }, [icon('fa-play'), ' Test']),
                    el('button', {
                        className: 'btn btn-sm btn-secondary',
                        on: { click: () => editPattern(eventType) }
                    }, [icon('fa-edit'), ' Edit'])
                ])
            ])));

            // Also update quick test grid if pattern tester is open
            this.updateQuickTestGrid(data.patterns);

        } catch (error) {
            console.error('Error loading patterns:', error);
            const patternsGrid = document.getElementById('patternsGrid');
            if (patternsGrid) {
                dom.replace(patternsGrid, dom.el('div', { className: 'loading', text: 'Error loading patterns' }));
            }
        }
    }

    async refreshPatterns() {
        const { el, replace, icon } = dom;

        try {
            // Show loading state
            const patternsGrid = document.getElementById('patternsGrid');
            if (patternsGrid) {
                replace(patternsGrid, el('div', { className: 'loading' }, [
                    el('i', { className: 'fas fa-sync-alt fa-spin', attrs: { 'aria-hidden': 'true' } }),
                    ' Refreshing patterns...'
                ]));
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
                const newPacks = dom.num(reloadData.newPacks);

                replace(patternsGrid, el('div', { className: 'refresh-success' }, [
                    icon('fa-check-circle'),
                    el('div', { text: 'Patterns refreshed successfully!' }),
                    el('div', { className: 'refresh-stats' }, [
                        `${dom.num(reloadData.totalPacks)} total packs loaded`,
                        newPacks > 0 ? ` (${newPacks} new)` : ''
                    ])
                ]));
            }

            // Wait a moment to show the success message
            await new Promise(resolve => setTimeout(resolve, 1500));

            // Then reload the patterns display
            await this.loadPatterns();

        } catch (error) {
            console.error('Error refreshing patterns:', error);
            const patternsGrid = document.getElementById('patternsGrid');
            if (patternsGrid) {
                replace(patternsGrid, el('div', { className: 'error-message' }, [
                    icon('fa-exclamation-triangle'),
                    el('div', { text: 'Failed to refresh patterns' }),
                    el('div', { className: 'error-details', text: error.message }),
                    el('button', {
                        className: 'btn btn-sm',
                        text: 'Try Again',
                        on: { click: () => app.loadPatterns() }
                    })
                ]));
            }
        }
    }

    updateQuickTestGrid(patterns) {
        const quickTestGrid = document.getElementById('quickTestGrid');
        if (!quickTestGrid || !patterns) return;

        const { el, replace, icon } = dom;

        replace(quickTestGrid, Object.entries(patterns).map(([eventType, pattern]) => el('div', { className: 'quick-test-item' }, [
            el('div', { className: 'test-item-header' }, [
                el('span', { className: 'test-item-name', text: pattern.Pattern.Name }),
                el('span', { className: 'test-item-event', text: eventType })
            ]),
            el('div', { className: 'test-item-details' }, [
                el('span', { text: `${pattern.Pattern.Frequency}Hz` }),
                el('span', { text: `${pattern.Pattern.Duration}ms` }),
                el('span', { text: `${pattern.Pattern.Intensity}%` })
            ]),
            el('button', {
                className: 'btn btn-sm btn-accent',
                on: { click: () => testPattern(eventType) }
            }, [icon('fa-play'), ' Test'])
        ])));
    }

    async loadAudioConfig() {
        try {
            const response = await fetch('/api/audio/devices');
            const data = await response.json();

            const deviceList = document.getElementById('audioDeviceList');
            if (!deviceList) return;

            const { el, replace } = dom;

            if (!data.devices) {
                replace(deviceList, el('div', { className: 'loading', text: 'No audio devices found' }));
                return;
            }

            replace(deviceList, data.devices.map(device => el('div', {
                className: `device-item ${isSelectedAudioDevice(device, data.current) ? 'active' : ''}`.trim(),
                on: { click: () => selectAudioDevice(device.endpointId || '', Number(device.id)) }
            }, [
                el('div', { className: 'device-info' }, [
                    el('div', { className: 'device-name' }, [
                        device.name,
                        device.isDefault ? ' (Default)' : ''
                    ]),
                    el('div', { className: 'device-driver' }, [
                        device.driver,
                        ` | ${dom.num(device.channels)} channels`
                    ])
                ]),
                el('i', {
                    className: device.isAvailable ? 'fas fa-check-circle' : 'fas fa-exclamation-circle',
                    style: { color: device.isAvailable ? 'var(--success-color)' : 'var(--warning-color)' },
                    attrs: { 'aria-hidden': 'true' }
                })
            ])));

        } catch (error) {
            console.error('Error loading audio config:', error);
            const deviceList = document.getElementById('audioDeviceList');
            if (deviceList) {
                dom.replace(deviceList, dom.el('div', { className: 'loading', text: 'Error loading audio devices' }));
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

            const { el, replace } = dom;
            const infoItem = (label, value, valueClass) => el('div', { className: 'info-item' }, [
                el('span', { className: 'label', text: label }),
                el('span', { className: valueClass || 'value', text: value })
            ]);

            if (pathInfo) {
                const files = data.recent_files || [];

                replace(pathInfo, [
                    infoItem(
                        'Path Status:',
                        data.path_exists ? 'Valid' : 'Not Found',
                        data.path_exists ? 'value online' : 'value'),
                    infoItem('Journal Files:', `${files.length} found`),
                    files.length > 0
                        ? el('div', { style: { marginTop: '1rem' } }, [
                            el('strong', { text: 'Recent Files:' }),
                            el('ul', { style: { marginTop: '0.5rem', paddingLeft: '1rem' } },
                                files.slice(0, 3).map(file => el('li', { text: file })))
                        ])
                        : null
                ]);
            }

            if (monitoringStatus) {
                replace(monitoringStatus, [
                    infoItem('Status:', data.status, data.monitoring ? 'value online' : 'value'),
                    infoItem('Health:', data.health),
                    infoItem('Events Processed:', data.events_processed),
                    infoItem(
                        'Last Event:',
                        data.last_event_time ? this.formatDateTime(data.last_event_time) : 'Never')
                ]);
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
                dom.replace(contextStatus, dom.el('div', { className: 'loading', text: 'Error loading context status' }));
            }
        }
    }

    updateContextStatus(context) {
        const contextStatus = document.getElementById('contextStatus');
        if (!contextStatus) return;

        const { el, replace, num, slug } = dom;

        // The state words also drive CSS classes, so they go through slug() - the visible copy is
        // still whatever the API said, as text.
        const item = (label, value, valueClass) => el('div', { className: 'context-item' }, [
            el('span', { className: 'context-label', text: label }),
            el('span', { className: valueClass || 'context-value', text: value })
        ]);

        replace(contextStatus, el('div', { className: 'context-grid' }, [
            item('Game State:', context.game_state, `context-value ${slug(context.game_state)}`.trim()),
            item('Current System:', context.current_system || 'Unknown'),
            item('Threat Level:', context.threat_level, `context-value threat-${slug(context.threat_level)}`),
            item(
                'Hull Integrity:',
                `${Math.round(num(context.hull_integrity) * 100)}%`,
                num(context.hull_integrity) < 0.5 ? 'context-value warning' : 'context-value'),
            item('Shield Strength:', `${Math.round(num(context.shield_strength) * 100)}%`),
            item('Combat Intensity:', `${Math.round(num(context.combat_intensity) * 100)}%`),
            item('Exploration Mode:', context.exploration_mode),
            item('Intensity Multiplier:', `${num(context.intensity_multiplier).toFixed(2)}x`)
        ]));
    }

    updateBehaviorAnalysis(context, statistics) {
        const behaviorAnalysis = document.getElementById('behaviorAnalysis');
        if (!behaviorAnalysis) return;

        const { el, replace, num } = dom;

        // Percentages are the only values that reach a style, so they are clamped to a number - a
        // string payload can never land inside the width declaration.
        const trait = (label, value, fillClass) => el('div', { className: 'trait-item' }, [
            el('span', { className: 'trait-label', text: label }),
            el('div', { className: 'trait-bar' }, [
                el('div', { className: fillClass, style: { width: `${num(value) * 100}%` } })
            ]),
            el('span', { className: 'trait-value', text: `${Math.round(num(value) * 100)}%` })
        ]);

        const stat = (label, value) => el('div', { className: 'stat-item' }, [
            el('span', { className: 'stat-label', text: label }),
            el('span', { className: 'stat-value', text: value })
        ]);

        replace(behaviorAnalysis, el('div', { className: 'behavior-grid' }, [
            el('div', { className: 'behavior-section' }, [
                el('h4', { text: 'Player Traits' }),
                trait('Aggressiveness:', context.player_aggressiveness, 'trait-fill'),
                trait('Cautiousness:', context.player_cautiousness, 'trait-fill cautious')
            ]),
            el('div', { className: 'behavior-section' }, [
                el('h4', { text: 'Activity Statistics' }),
                stat('Systems Visited:', statistics.systems_visited),
                stat('Bodies Scanned:', statistics.bodies_scanned),
                stat('Recent Events:', (statistics.recent_event_types || []).join(', '))
            ]),
            el('div', { className: 'behavior-section' }, [
                el('h4', { text: 'Time Distribution' }),
                el('div', { className: 'time-distribution' },
                    (statistics.state_time_spent || []).map(state => el('div', { className: 'time-item' }, [
                        el('span', { className: 'time-label', text: `${state.state}:` }),
                        el('span', { className: 'time-value', text: `${Math.round(num(state.time_minutes))} min` })
                    ])))
            ])
        ]));
    }

    updatePredictions(predictions) {
        const predictionsDiv = document.getElementById('predictions');
        if (!predictionsDiv) return;

        const { el, replace, num } = dom;
        const upcoming = predictions.likely_upcoming_events || [];

        const item = (label, value) => el('div', { className: 'prediction-item' }, [
            el('span', { className: 'prediction-label', text: label }),
            el('span', { className: 'prediction-value', text: value })
        ]);

        replace(predictionsDiv, el('div', { className: 'predictions-grid' }, [
            el('div', { className: 'prediction-section' }, [
                el('h4', { text: 'Next State Prediction' }),
                item('Predicted State:', predictions.predicted_next_state || 'None'),
                item('Confidence:', `${Math.round(num(predictions.prediction_confidence) * 100)}%`)
            ]),
            el('div', { className: 'prediction-section' }, [
                el('h4', { text: 'Likely Upcoming Events' }),
                el('div', { className: 'events-list' },
                    upcoming.length > 0
                        ? upcoming.map(event => el('div', { className: 'event-prediction', text: event }))
                        : el('div', { className: 'no-predictions', text: 'No predictions available' }))
            ])
        ]));
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
        // A toast is the only report some actions give, so it is a live region: errors interrupt
        // (alert), everything else waits for a pause (status).
        const toast = dom.el('div', {
            className: `toast ${type}`,
            attrs: {
                role: type === 'error' ? 'alert' : 'status',
                'aria-atomic': 'true'
            }
        });
        // Toast text is frequently an error string straight off the API - shown, never parsed.
        dom.append(toast, dom.el('div', {
            style: { display: 'flex', alignItems: 'center', gap: '0.5rem' }
        }, [
            dom.icon(type === 'success' ? 'fa-check-circle' : type === 'error' ? 'fa-exclamation-circle' : 'fa-info-circle'),
            message
        ]));

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

// ----- Modal dialogs -----
//
// Both modals are role="dialog" aria-modal="true". While one is open, Tab stays inside it, Escape
// closes it, and whatever was focused before it opened gets the focus back - otherwise a screen
// reader or keyboard user is left behind the dialog with no way back.
const DIALOG_FOCUSABLE = [
    'a[href]',
    'button:not([disabled])',
    'input:not([disabled])',
    'select:not([disabled])',
    'textarea:not([disabled])',
    '[tabindex]:not([tabindex="-1"])'
].join(', ');

let activeDialog = null;
let dialogReturnFocus = null;

function dialogFocusables(dialog) {
    return Array.from(dialog.querySelectorAll(DIALOG_FOCUSABLE)).filter(node => !node.closest('[hidden]'));
}

function focusIntoDialog(dialog) {
    const focusables = dialogFocusables(dialog);
    // With nothing focusable inside - a dialog still loading its body - the heading takes focus so
    // the reader at least starts where the dialog does.
    const target = focusables[0] || dialog.querySelector('.modal-header h3') || dialog;
    if (focusables.length === 0) target.setAttribute('tabindex', '-1');
    target.focus();
}

function openDialog(id) {
    const dialog = document.getElementById(id);
    if (!dialog) return null;

    // Reopening the wizard on a later step must not steal the caller's place in the dialog, nor
    // forget where focus came from originally.
    const alreadyOpen = dialog === activeDialog;
    if (!alreadyOpen) {
        dialogReturnFocus = document.activeElement;
        activeDialog = dialog;
    }

    dialog.hidden = false;
    dialog.classList.add('active');

    if (!alreadyOpen) focusIntoDialog(dialog);
    return dialog;
}

function closeDialog(id) {
    const dialog = document.getElementById(id);
    if (!dialog) return;

    dialog.classList.remove('active');
    dialog.hidden = true;

    if (dialog !== activeDialog) return;
    activeDialog = null;

    const restore = dialogReturnFocus;
    dialogReturnFocus = null;
    if (restore && typeof restore.focus === 'function' && document.contains(restore)) {
        restore.focus();
    }
}

document.addEventListener('keydown', (event) => {
    const dialog = activeDialog;
    if (!dialog) return;

    if (event.key === 'Escape') {
        event.preventDefault();
        closeDialog(dialog.id);
        return;
    }

    if (event.key !== 'Tab') return;

    const focusables = dialogFocusables(dialog);
    if (focusables.length === 0) {
        event.preventDefault();
        return;
    }

    const first = focusables[0];
    const last = focusables[focusables.length - 1];
    const active = document.activeElement;

    if (event.shiftKey && (active === first || !dialog.contains(active))) {
        event.preventDefault();
        last.focus();
    } else if (!event.shiftKey && (active === last || !dialog.contains(active))) {
        event.preventDefault();
        first.focus();
    }
});

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
    // Navigate to pattern editor with the specific event type
    window.location.href = `pattern-editor.html?event=${encodeURIComponent(eventType)}&mode=edit`;
};

window.createNewPattern = () => {
    // TODO: Implement new pattern creation
    app.showToast('New pattern creation - Coming soon!', 'warning');
};

// The endpoint id is the device identity, so it decides the highlight whenever the API reports
// one; the numeric id only addresses the list that was just returned. isSelected, when the API
// sends it, already accounts for a saved device that is no longer connected.
function isSelectedAudioDevice(device, current) {
    if (typeof device.isSelected === 'boolean') return device.isSelected;
    if (current && current.endpointId) return device.endpointId === current.endpointId;
    return current ? device.id === current.id : false;
}

// Endpoint ids contain braces and dots, so they are JSON-encoded - and then escaped for the
// attribute they land in - instead of being pasted into onclick raw.
function audioDeviceArgs(device) {
    const endpointId = JSON.stringify(device.endpointId || '')
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/"/g, '&quot;');

    return `${endpointId}, ${Number(device.id)}`;
}

window.selectAudioDevice = async (endpointId, deviceId) => {
    try {
        const response = await fetch('/api/audio/device', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            // The endpoint id survives reordering; the numeric id is only sent as a fallback for
            // the system default entry, which has no endpoint of its own.
            body: JSON.stringify(endpointId ? { endpointId, deviceId } : { deviceId })
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

// The server only answers 2xx when the tone actually reached an open output, so the toast repeats
// what it said rather than claiming success because a request was accepted.
window.testAudio = async () => {
    try {
        const response = await fetch('/api/audio/test', {
            method: 'POST'
        });

        const result = await response.json().catch(() => ({}));

        if (response.ok) {
            app.showToast(result.message || 'Audio test played.', 'success');
        } else {
            app.showToast(result.error || 'The audio test could not be played', 'error');
        }
    } catch (error) {
        console.error('Error testing audio:', error);
        app.showToast('Error testing audio', 'error');
    }
};

// The way out of a tone that is too strong: stops everything already playing, immediately.
window.stopAudio = async () => {
    try {
        const response = await fetch('/api/audio/stop', {
            method: 'POST'
        });

        const result = await response.json().catch(() => ({}));

        if (response.ok) {
            app.showToast(result.message || 'Playback stopped.', 'success');
        } else {
            app.showToast(result.error || 'Error stopping playback', 'error');
        }
    } catch (error) {
        console.error('Error stopping playback:', error);
        app.showToast('Error stopping playback', 'error');
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
window.openPatternModal = () => openDialog('patternModal');

window.closePatternModal = () => closeDialog('patternModal');

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

    const { el } = dom;
    const layerIndex = layerControls.children.length;

    // The range inputs are wired here rather than through an inline oninput, which the CSP blocks.
    const range = (className, attrs, initial) => {
        const input = el('input', { className, attrs: { type: 'range', ...attrs }, value: initial });
        const display = el('span', { className: 'range-value', text: initial });

        input.addEventListener('input', () => { display.textContent = input.value; });

        return [input, display];
    };

    const layerDiv = el('div', { className: 'layer-control' }, [
        el('div', { className: 'layer-header' }, [
            el('h6', { text: `Layer ${layerIndex + 1}` }),
            el('button', {
                className: 'btn-remove',
                attrs: { type: 'button' },
                text: '\u00d7',
                on: { click: (event) => removeLayer(event.currentTarget) }
            })
        ]),
        el('div', { className: 'layer-params' }, [
            el('div', { className: 'form-group' }, [
                el('label', { text: 'Waveform' }),
                el('select', { className: 'layer-waveform' },
                    ['Sine', 'Square', 'Triangle', 'Sawtooth', 'Noise']
                        .map(waveform => el('option', { attrs: { value: waveform }, text: waveform })))
            ]),
            el('div', { className: 'form-group' }, [
                el('label', { text: 'Frequency (Hz)' }),
                ...range('layer-frequency', { min: '20', max: '80' }, '40')
            ]),
            el('div', { className: 'form-group' }, [
                el('label', { text: 'Amplitude' }),
                ...range('layer-amplitude', { min: '0.1', max: '1.0', step: '0.1' }, '0.8')
            ])
        ])
    ]);

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
        dom.clear(layerControls);
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
        dom.replace(startBtn, [dom.icon('fa-play'), isReplaying ? ' Running...' : ' Start Replay']);
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
                dom.clear(journalFileSelect);
                
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
            dom.replace(journalFileSelect, dom.el('option', {
                attrs: { value: '' },
                text: 'Error loading journal files'
            }));
        }
    }
};

// ----- First-run setup wizard controls -----

// Reopening is explicit and non-destructive: the persisted completion record stays as it is.
window.openSetupWizard = async () => {
    try {
        const response = await fetch('/api/setup/reopen', { method: 'POST' });
        if (response.ok) {
            const result = await response.json();
            app.applySetupStatus(result.setup);
        }
    } catch (error) {
        console.error('Error reopening setup wizard:', error);
    }

    app.openSetupWizard();
};

window.closeSetupWizard = () => app.closeSetupWizard();

window.showSetupStep = (stepId) => app.openSetupWizard(stepId);

window.refreshHealth = () => app.updateSystemStatus();

window.confirmJournalPath = async (path) => {
    const input = document.getElementById('setupJournalPath');
    const chosen = path || (input ? input.value.trim() : '');

    if (!chosen) {
        app.showToast('Enter or pick a journal folder first', 'warning');
        return;
    }

    try {
        const response = await fetch('/api/setup/journal', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ path: chosen })
        });

        const result = await response.json();

        if (!response.ok) {
            app.showToast(result.error || 'Could not use that folder', 'error');
            return;
        }

        app.showToast(result.warning || `Journal folder set to ${result.path}`, result.warning ? 'warning' : 'success');
        app.applySetupStatus(result.setup);
        app.openSetupWizard('audio-device');
    } catch (error) {
        console.error('Error confirming journal path:', error);
        app.showToast('Error confirming journal folder', 'error');
    }
};

window.selectSetupAudioDevice = async (endpointId, deviceId) => {
    try {
        const response = await fetch('/api/setup/audio/device', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(endpointId ? { endpointId, deviceId } : { deviceId })
        });

        const result = await response.json();

        if (!response.ok) {
            app.showToast(result.error || 'Could not select that device', 'error');
            return;
        }

        app.showToast(`Output set to ${result.device.name}`, 'success');
        app.applySetupStatus(result.setup);
        app.openSetupWizard('audio-test');
    } catch (error) {
        console.error('Error selecting audio device:', error);
        app.showToast('Error selecting the output device', 'error');
    }
};

window.runSetupAudioTest = async () => {
    const resultBox = document.getElementById('setupTestResult');
    if (resultBox) resultBox.textContent = 'Playing the test tone...';

    try {
        const response = await fetch('/api/setup/audio/test', { method: 'POST' });
        const result = await response.json();

        if (!response.ok) {
            app.showToast(result.error || 'Audio test failed', 'error');
            if (resultBox) resultBox.textContent = result.error || 'Audio test failed.';
            return;
        }

        // played is false when no device could be opened; say so rather than claiming success.
        if (resultBox) resultBox.textContent = result.reason;
        app.showToast(result.reason, result.played ? 'success' : 'warning');
        app.applySetupStatus(result.setup);
        app.openSetupWizard(result.played ? 'finish' : 'audio-test');
    } catch (error) {
        console.error('Error running the audio test:', error);
        app.showToast('Error running the audio test', 'error');
    }
};

window.completeSetup = async () => {
    try {
        const response = await fetch('/api/setup/complete', { method: 'POST' });
        const result = await response.json();

        if (!response.ok) {
            app.showToast(result.error || 'Could not save setup', 'error');
            return;
        }

        app.applySetupStatus(result.setup);
        app.closeSetupWizard();
        app.showToast('Setup saved. Reopen it any time from the dashboard.', 'success');
    } catch (error) {
        console.error('Error completing setup:', error);
        app.showToast('Error saving setup', 'error');
    }
};

window.retryHealthComponent = async (componentId) => {
    try {
        const response = await fetch(`/api/health/${componentId}/retry`, { method: 'POST' });
        const result = await response.json();

        if (!response.ok) {
            app.showToast(result.error || 'Retry failed', 'error');
            return;
        }

        app.renderHealth(result.health);
        app.showToast(result.component.reason, result.component.status === 'ok' ? 'success' : 'warning');
    } catch (error) {
        console.error('Error retrying health component:', error);
        app.showToast('Error retrying', 'error');
    }
};

// Initialize app when DOM is loaded
let app;
document.addEventListener('DOMContentLoaded', () => {
    app = new ButtkickerApp();
});