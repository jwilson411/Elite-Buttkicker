class TimelineEditor {
    constructor() {
        this.container = null;
        this.canvas = null;
        this.ctx = null;
        this.layers = [];
        this.controlPoints = [];
        this.selectedPoint = null;
        this.selectedPointIndex = -1;
        this.selectedLayer = null;
        this.isDragging = false;
        this.isPanning = false;
        this.isPlaying = false;
        this.lastPanX = 0;
        this.spacePressed = false;
        this.currentTime = 0;
        this.duration = 3000;
        this.zoom = 1.0;
        this.panX = 0;
        this.gridTimeStep = 250;
        this.gridIntensityStep = 10;
        this.globalCurveType = 'Linear';
        this.renderScheduled = false;
        this.layerColors = ['#ff6b35', '#f7931e', '#00bcd4', '#4caf50', '#ff9800'];
        this.waveformTypes = ['Sine', 'Square', 'Triangle', 'Sawtooth', 'Noise'];
        this.curveTypes = ['Linear', 'Exponential', 'Logarithmic', 'Sine', 'Bounce', 'Custom'];

        this.callbacks = {
            onPatternChanged: () => {},
            onLayerAdded: () => {},
            onLayerRemoved: () => {}
        };

        // Resolve CSS colors for canvas use
        this.initializeColors();
    }

    initializeColors() {
        const rootStyle = getComputedStyle(document.documentElement);
        this.colors = {
            accent: rootStyle.getPropertyValue('--accent-color').trim() || '#00bcd4',
            primary: rootStyle.getPropertyValue('--primary-color').trim() || '#ff6b35',
            timelineCursor: rootStyle.getPropertyValue('--timeline-cursor').trim() || 'rgba(255, 107, 53, 0.8)'
        };
    }

    initialize(container, initialPattern = null) {
        this.container = container;
        this.createUI();
        this.setupCanvas();
        this.setupEventListeners();

        if (initialPattern) {
            this.setHapticPattern(initialPattern);
        } else {
            this.addDefaultLayer();
        }

        this.render();
        return this;
    }

    createUI() {
        this.container.innerHTML = `
            <div class="timeline-editor-main">
                <div class="timeline-toolbar" role="toolbar" aria-label="Timeline Editor Controls">
                    <div class="timeline-controls" role="group" aria-label="Playback Controls">
                        <button id="playBtn" class="control-btn" aria-label="Play timeline">▶️</button>
                        <button id="pauseBtn" class="control-btn" style="display: none;" aria-label="Pause timeline">⏸️</button>
                        <button id="stopBtn" class="control-btn" aria-label="Stop timeline">⏹️</button>
                        <span class="time-display" aria-live="polite" aria-label="Current time">0.0s / ${(this.duration / 1000).toFixed(1)}s</span>
                    </div>
                    <div class="duration-controls" role="group" aria-label="Pattern Duration Controls">
                        <label for="durationInput">Duration:</label>
                        <input type="number" id="durationInput" min="100" max="30000" step="100" value="${this.duration}"
                               aria-label="Pattern duration in milliseconds" style="width: 80px;">
                        <span>ms</span>
                    </div>
                    <div class="zoom-controls" role="group" aria-label="Zoom Controls">
                        <button id="zoomInBtn" class="control-btn" aria-label="Zoom in">🔍+</button>
                        <button id="zoomOutBtn" class="control-btn" aria-label="Zoom out">🔍-</button>
                        <button id="resetZoomBtn" class="control-btn" aria-label="Reset zoom">↻</button>
                    </div>
                    <div class="curve-tools" role="group" aria-label="Curve Tools">
                        <label for="curveTypeSelect">Curve Type:</label>
                        <select id="curveTypeSelect" aria-label="Select curve type">
                            ${this.curveTypes.map(type => `<option value="${type}">${type}</option>`).join('')}
                        </select>
                        <button id="addPointBtn" class="control-btn" aria-label="Add control point">+ Point</button>
                    </div>
                </div>

                <div class="timeline-content">
                    <div class="timeline-canvas-wrapper">
                        <canvas id="timelineCanvas" class="timeline-canvas"
                                role="img"
                                aria-label="Timeline visualization canvas. Use arrow keys to navigate, Space to pan, mouse to add control points"
                                tabindex="0"></canvas>
                    </div>

                    <div class="layer-panel" role="complementary" aria-label="Layer Management Panel">
                        <div class="layer-panel-header">
                            <h3>Layers</h3>
                            <button id="addLayerBtn" class="control-btn" aria-label="Add new layer">+ Add Layer</button>
                        </div>
                        <div id="layerList" class="layer-list" role="list" aria-label="Timeline layers"></div>

                        <div class="layer-controls" id="layerControls" style="display: none;" role="region" aria-label="Layer Properties">
                            <h4>Layer Properties</h4>
                            <div class="control-group">
                                <label for="layerWaveform">Waveform:</label>
                                <select id="layerWaveform" aria-label="Select waveform type">
                                    ${this.waveformTypes.map(type => `<option value="${type}">${type}</option>`).join('')}
                                </select>
                            </div>
                            <div class="control-group">
                                <label for="frequencySlider">Frequency: <span id="frequencyValue" aria-live="polite">40</span>Hz</label>
                                <input type="range" id="frequencySlider" min="20" max="120" value="40" step="1"
                                       aria-label="Frequency slider" aria-describedby="frequencyValue">
                            </div>
                            <div class="control-group">
                                <label for="amplitudeSlider">Amplitude: <span id="amplitudeValue" aria-live="polite">100</span>%</label>
                                <input type="range" id="amplitudeSlider" min="0" max="100" value="100" step="1"
                                       aria-label="Amplitude slider" aria-describedby="amplitudeValue">
                            </div>
                            <div class="control-group">
                                <label for="phaseSlider">Phase: <span id="phaseValue" aria-live="polite">0</span>°</label>
                                <input type="range" id="phaseSlider" min="0" max="360" value="0" step="1"
                                       aria-label="Phase slider" aria-describedby="phaseValue">
                            </div>
                            <div class="control-group">
                                <label for="startTimeSlider">Start Time: <span id="startTimeValue" aria-live="polite">0</span>ms</label>
                                <input type="range" id="startTimeSlider" min="0" max="${this.duration}" value="0" step="10"
                                       aria-label="Start time slider" aria-describedby="startTimeValue">
                            </div>
                            <div class="control-group">
                                <label for="layerDurationSlider">Duration: <span id="layerDurationValue" aria-live="polite">${this.duration}</span>ms</label>
                                <input type="range" id="layerDurationSlider" min="100" max="${this.duration}" value="${this.duration}" step="10"
                                       aria-label="Duration slider" aria-describedby="layerDurationValue">
                            </div>
                            <div class="control-group">
                                <label for="fadeInSlider">Fade In: <span id="fadeInValue" aria-live="polite">0</span>ms</label>
                                <input type="range" id="fadeInSlider" min="0" max="1000" value="0" step="10"
                                       aria-label="Fade in slider" aria-describedby="fadeInValue">
                            </div>
                            <div class="control-group">
                                <label for="fadeOutSlider">Fade Out: <span id="fadeOutValue" aria-live="polite">0</span>ms</label>
                                <input type="range" id="fadeOutSlider" min="0" max="1000" value="0" step="10"
                                       aria-label="Fade out slider" aria-describedby="fadeOutValue">
                            </div>
                            <div class="control-group">
                                <label for="layerCurve">Curve Type:</label>
                                <select id="layerCurve" aria-label="Select layer curve type">
                                    ${this.curveTypes.map(type => `<option value="${type}">${type}</option>`).join('')}
                                </select>
                            </div>
                            <div class="layer-actions" role="group" aria-label="Layer Actions">
                                <button id="duplicateLayerBtn" class="control-btn" aria-label="Duplicate current layer">Duplicate</button>
                                <button id="removeLayerBtn" class="control-btn danger" aria-label="Remove current layer">Remove</button>
                            </div>
                        </div>
                    </div>
                </div>
                <!-- Offscreen aria-live element for control point selection announcements -->
                <div class="sr-only-announce" role="status" aria-live="polite" style="position: absolute; left: -10000px; width: 1px; height: 1px; overflow: hidden;"></div>
            </div>
        `;
    }

    setupCanvas() {
        this.canvas = this.safeQuery('#timelineCanvas');
        if (!this.canvas) {
            console.error('Timeline Editor: Canvas element not found');
            return;
        }

        this.ctx = this.canvas.getContext('2d');
        if (!this.ctx) {
            console.error('Timeline Editor: Failed to get 2D context');
            return;
        }

        // Set canvas size
        const wrapper = this.safeQuery('.timeline-canvas-wrapper');
        const rect = this.canvas.getBoundingClientRect();

        // Store logical dimensions
        this.logicalWidth = rect.width || (wrapper ? wrapper.clientWidth - 20 : 800);
        this.logicalHeight = rect.height || 300;

        // Handle high DPI displays
        const dpr = window.devicePixelRatio || 1;
        this.canvas.width = this.logicalWidth * dpr;
        this.canvas.height = this.logicalHeight * dpr;
        this.canvas.style.width = this.logicalWidth + 'px';
        this.canvas.style.height = this.logicalHeight + 'px';
        this.ctx.scale(dpr, dpr);

        // Canvas event listeners
        this.canvas.addEventListener('mousedown', this.handleCanvasMouseDown.bind(this));
        this.canvas.addEventListener('mousemove', this.handleCanvasMouseMove.bind(this));
        this.canvas.addEventListener('mouseup', this.handleCanvasMouseUp.bind(this));
        this.canvas.addEventListener('contextmenu', this.handleCanvasContextMenu.bind(this));
        this.canvas.addEventListener('wheel', this.handleCanvasWheel.bind(this));
        this.canvas.addEventListener('keydown', this.handleCanvasKeyDown.bind(this));

        // Global keyboard events for panning
        window.addEventListener('keydown', this.handleKeyDown.bind(this));
        window.addEventListener('keyup', this.handleKeyUp.bind(this));

        window.addEventListener('resize', this.handleResize.bind(this));
    }

    setupEventListeners() {
        // Store bound handlers for cleanup
        this.boundHandlers = {
            click: this.handleContainerClick.bind(this),
            input: this.handleContainerInput.bind(this),
            change: this.handleContainerChange.bind(this)
        };

        this.container.addEventListener('click', this.boundHandlers.click);
        this.container.addEventListener('input', this.boundHandlers.input);
        this.container.addEventListener('change', this.boundHandlers.change);
    }

    // Safe element query with null checking
    safeQuery(selector, context = this.container) {
        try {
            return context ? context.querySelector(selector) : null;
        } catch (error) {
            console.warn(`Timeline Editor: Failed to query selector "${selector}":`, error);
            return null;
        }
    }

    handleContainerClick(e) {
        const addLayerBtn = this.safeQuery('#addLayerBtn');
        const removeLayerBtn = this.safeQuery('#removeLayerBtn');
        const duplicateLayerBtn = this.safeQuery('#duplicateLayerBtn');
        const playBtn = this.safeQuery('#playBtn');
        const pauseBtn = this.safeQuery('#pauseBtn');
        const stopBtn = this.safeQuery('#stopBtn');
        const zoomInBtn = this.safeQuery('#zoomInBtn');
        const zoomOutBtn = this.safeQuery('#zoomOutBtn');
        const resetZoomBtn = this.safeQuery('#resetZoomBtn');
        const addPointBtn = this.safeQuery('#addPointBtn');

        if (e.target === addLayerBtn && addLayerBtn) this.addLayer();
        if (e.target === removeLayerBtn && removeLayerBtn) this.removeSelectedLayer();
        if (e.target === duplicateLayerBtn && duplicateLayerBtn) this.duplicateSelectedLayer();
        if (e.target === playBtn && playBtn) this.play();
        if (e.target === pauseBtn && pauseBtn) this.pause();
        if (e.target === stopBtn && stopBtn) this.stop();
        if (e.target === zoomInBtn && zoomInBtn) this.zoomIn();
        if (e.target === zoomOutBtn && zoomOutBtn) this.zoomOut();
        if (e.target === resetZoomBtn && resetZoomBtn) this.resetZoom();
        if (e.target === addPointBtn && addPointBtn) this.addControlPoint();
    }

    handleContainerInput(e) {
        const frequencySlider = this.safeQuery('#frequencySlider');
        const amplitudeSlider = this.safeQuery('#amplitudeSlider');
        const phaseSlider = this.safeQuery('#phaseSlider');
        const startTimeSlider = this.safeQuery('#startTimeSlider');
        const layerDurationSlider = this.safeQuery('#layerDurationSlider');
        const fadeInSlider = this.safeQuery('#fadeInSlider');
        const fadeOutSlider = this.safeQuery('#fadeOutSlider');
        const durationInput = this.safeQuery('#durationInput');

        if (e.target === durationInput && durationInput) {
            this.updatePatternDuration(parseInt(e.target.value));
        }
        if (e.target === frequencySlider && frequencySlider) {
            this.updateLayerProperty('frequency', parseInt(e.target.value));
        }
        if (e.target === amplitudeSlider && amplitudeSlider) {
            this.updateLayerProperty('amplitude', parseInt(e.target.value));
        }
        if (e.target === phaseSlider && phaseSlider) {
            this.updateLayerProperty('phase', parseInt(e.target.value));
        }
        if (e.target === startTimeSlider && startTimeSlider) {
            this.updateLayerProperty('startTime', parseInt(e.target.value));
        }
        if (e.target === layerDurationSlider && layerDurationSlider) {
            this.updateLayerProperty('duration', parseInt(e.target.value));
        }
        if (e.target === fadeInSlider && fadeInSlider) {
            this.updateLayerProperty('fadeIn', parseInt(e.target.value));
        }
        if (e.target === fadeOutSlider && fadeOutSlider) {
            this.updateLayerProperty('fadeOut', parseInt(e.target.value));
        }
    }

    handleContainerChange(e) {
        const layerWaveform = this.safeQuery('#layerWaveform');
        const layerCurve = this.safeQuery('#layerCurve');
        const curveTypeSelect = this.safeQuery('#curveTypeSelect');

        if (e.target === layerWaveform && layerWaveform) {
            this.updateLayerProperty('waveform', e.target.value);
        }
        if (e.target === layerCurve && layerCurve) {
            this.updateLayerProperty('curve', e.target.value);
        }
        if (e.target === curveTypeSelect && curveTypeSelect) {
            this.updateCurveType(e.target.value);
        }
    }

    // Cleanup method to remove event listeners
    destroy() {
        if (this.container && this.boundHandlers) {
            this.container.removeEventListener('click', this.boundHandlers.click);
            this.container.removeEventListener('input', this.boundHandlers.input);
            this.container.removeEventListener('change', this.boundHandlers.change);
        }

        // Remove window event listeners
        window.removeEventListener('keydown', this.handleKeyDown.bind(this));
        window.removeEventListener('keyup', this.handleKeyUp.bind(this));
        window.removeEventListener('resize', this.handleResize.bind(this));

        // Clear intervals
        if (this.playbackInterval) {
            clearInterval(this.playbackInterval);
        }
    }

    addDefaultLayer() {
        this.addLayer({
            waveform: 'Sine',
            frequency: 40,
            amplitude: 100,
            phase: 0,
            startTime: 0,
            duration: this.duration,
            fadeIn: 0,
            fadeOut: 0
        });
    }

    addLayer(properties = {}) {
        const layer = {
            id: Date.now() + Math.random(),
            waveform: properties.waveform || 'Sine',
            frequency: properties.frequency || 40,
            amplitude: properties.amplitude || 100,
            phase: properties.phase || 0,
            startTime: properties.startTime || 0,
            duration: properties.duration || this.duration,
            fadeIn: properties.fadeIn || 0,
            fadeOut: properties.fadeOut || 0,
            curve: properties.curve || 'Linear',
            color: this.layerColors[this.layers.length % this.layerColors.length],
            visible: true,
            muted: false
        };

        this.layers.push(layer);
        this.updateLayerList();
        this.selectLayer(layer);
        this.render();
        this.callbacks.onLayerAdded(layer);
        this.callbacks.onPatternChanged();
    }

    removeSelectedLayer() {
        if (!this.selectedLayer) return;

        const index = this.layers.findIndex(l => l.id === this.selectedLayer.id);
        if (index !== -1) {
            this.layers.splice(index, 1);
            this.selectedLayer = null;
            this.updateLayerList();
            this.updateLayerControls();
            this.render();
            this.callbacks.onLayerRemoved();
            this.callbacks.onPatternChanged();
        }
    }

    duplicateSelectedLayer() {
        if (!this.selectedLayer) return;

        const duplicate = { ...this.selectedLayer };
        duplicate.id = Date.now() + Math.random();
        duplicate.startTime += 100; // Offset slightly
        this.layers.push(duplicate);
        this.selectLayer(duplicate);
        this.updateLayerList();
        this.render();
        this.callbacks.onLayerAdded(duplicate);
        this.callbacks.onPatternChanged();
    }

    selectLayer(layer) {
        this.selectedLayer = layer;
        this.updateLayerList();
        this.updateLayerControls();
        this.render();
    }

    updateLayerList() {
        const layerList = this.safeQuery('#layerList');
        if (!layerList) {
            console.warn('Timeline Editor: Layer list element not found');
            return;
        }

        layerList.innerHTML = this.layers.map((layer, index) => `
            <div class="layer-item ${this.selectedLayer?.id === layer.id ? 'selected' : ''}"
                 data-layer-id="${layer.id}"
                 role="listitem"
                 tabindex="0"
                 aria-label="Layer ${index + 1}: ${layer.waveform} waveform, ${layer.visible ? 'visible' : 'hidden'}, ${layer.muted ? 'muted' : 'unmuted'}"
                 ${this.selectedLayer?.id === layer.id ? 'aria-selected="true"' : 'aria-selected="false"'}>
                <div class="layer-header">
                    <div class="layer-color" style="background-color: ${layer.color}" aria-hidden="true"></div>
                    <span class="layer-name">Layer ${index + 1} (${layer.waveform})</span>
                    <div class="layer-toggles" role="group" aria-label="Layer visibility controls">
                        <button class="toggle-btn ${layer.visible ? 'active' : ''}"
                                data-action="toggle-visible"
                                aria-label="${layer.visible ? 'Hide layer' : 'Show layer'}"
                                aria-pressed="${layer.visible}">👁️</button>
                        <button class="toggle-btn ${layer.muted ? 'active' : ''}"
                                data-action="toggle-muted"
                                aria-label="${layer.muted ? 'Unmute layer' : 'Mute layer'}"
                                aria-pressed="${layer.muted}">🔇</button>
                    </div>
                </div>
                <div class="layer-preview">
                    <canvas class="layer-waveform" width="200" height="30"
                            role="img"
                            aria-label="Waveform preview for layer ${index + 1}"></canvas>
                </div>
            </div>
        `).join('');

        // Add layer selection event listeners
        layerList.querySelectorAll('.layer-item').forEach(item => {
            item.addEventListener('click', (e) => {
                if (e.target.classList.contains('toggle-btn')) return;
                const layerId = item.dataset.layerId;
                const layer = this.layers.find(l => l.id == layerId);
                if (layer) this.selectLayer(layer);
            });

            item.addEventListener('keydown', (e) => {
                if (e.target.classList.contains('toggle-btn')) return;

                const layerId = item.dataset.layerId;
                const layer = this.layers.find(l => l.id == layerId);

                switch (e.code) {
                    case 'Enter':
                    case 'Space':
                        if (layer) this.selectLayer(layer);
                        e.preventDefault();
                        break;
                    case 'ArrowUp':
                        const prevItem = item.previousElementSibling;
                        if (prevItem) {
                            prevItem.focus();
                            e.preventDefault();
                        }
                        break;
                    case 'ArrowDown':
                        const nextItem = item.nextElementSibling;
                        if (nextItem) {
                            nextItem.focus();
                            e.preventDefault();
                        }
                        break;
                    case 'Delete':
                    case 'Backspace':
                        if (layer && this.layers.length > 1) {
                            this.removeLayer(layer);
                            e.preventDefault();
                        }
                        break;
                    case 'KeyV':
                        if (layer) {
                            layer.visible = !layer.visible;
                            this.updateLayerList();
                            this.render();
                            this.callbacks.onPatternChanged();
                            e.preventDefault();
                        }
                        break;
                    case 'KeyM':
                        if (layer) {
                            layer.muted = !layer.muted;
                            this.updateLayerList();
                            this.render();
                            this.callbacks.onPatternChanged();
                            e.preventDefault();
                        }
                        break;
                }
            });
        });

        // Add toggle event listeners
        layerList.querySelectorAll('.toggle-btn').forEach(btn => {
            btn.addEventListener('click', (e) => {
                e.stopPropagation();
                const layerId = btn.closest('.layer-item').dataset.layerId;
                const layer = this.layers.find(l => l.id == layerId);
                const action = btn.dataset.action;

                if (action === 'toggle-visible') {
                    layer.visible = !layer.visible;
                } else if (action === 'toggle-muted') {
                    layer.muted = !layer.muted;
                }

                this.updateLayerList();
                this.render();
                this.callbacks.onPatternChanged();
            });
        });

        // Draw waveform previews
        this.drawLayerPreviews();
    }

    drawLayerPreviews() {
        this.container.querySelectorAll('.layer-waveform').forEach((canvas, index) => {
            const layer = this.layers[index];
            if (!layer) return;

            const ctx = canvas.getContext('2d');
            ctx.clearRect(0, 0, canvas.width, canvas.height);

            ctx.strokeStyle = layer.color;
            ctx.lineWidth = 1;
            ctx.beginPath();

            const samples = canvas.width;
            const centerY = canvas.height / 2;

            for (let i = 0; i < samples; i++) {
                const time = (i / samples) * layer.duration;
                const phase = (time / 1000) * layer.frequency * 2 * Math.PI + (layer.phase * Math.PI / 180);
                let value = 0;

                switch (layer.waveform) {
                    case 'Sine':
                        value = Math.sin(phase);
                        break;
                    case 'Square':
                        value = Math.sin(phase) > 0 ? 1 : -1;
                        break;
                    case 'Triangle':
                        value = (2 / Math.PI) * Math.asin(Math.sin(phase));
                        break;
                    case 'Sawtooth':
                        value = (2 / Math.PI) * Math.atan(Math.tan(phase / 2));
                        break;
                    case 'Noise':
                        value = Math.random() * 2 - 1;
                        break;
                }

                const y = centerY + (value * centerY * 0.8);
                if (i === 0) {
                    ctx.moveTo(i, y);
                } else {
                    ctx.lineTo(i, y);
                }
            }

            ctx.stroke();
        });
    }

    updateLayerControls() {
        const controls = this.safeQuery('#layerControls');
        if (!controls) return;

        if (!this.selectedLayer) {
            controls.style.display = 'none';
            return;
        }

        controls.style.display = 'block';
        const layer = this.selectedLayer;

        // Update control values with null checks
        const elements = [
            ['#layerWaveform', 'value', layer.waveform],
            ['#frequencySlider', 'value', layer.frequency],
            ['#frequencyValue', 'textContent', layer.frequency],
            ['#amplitudeSlider', 'value', layer.amplitude],
            ['#amplitudeValue', 'textContent', layer.amplitude],
            ['#phaseSlider', 'value', layer.phase],
            ['#phaseValue', 'textContent', layer.phase],
            ['#startTimeSlider', 'value', layer.startTime],
            ['#startTimeValue', 'textContent', layer.startTime],
            ['#layerDurationSlider', 'value', layer.duration],
            ['#layerDurationValue', 'textContent', layer.duration],
            ['#fadeInSlider', 'value', layer.fadeIn],
            ['#fadeInValue', 'textContent', layer.fadeIn],
            ['#fadeOutSlider', 'value', layer.fadeOut],
            ['#fadeOutValue', 'textContent', layer.fadeOut],
            ['#layerCurve', 'value', layer.curve]
        ];

        elements.forEach(([selector, property, value]) => {
            const element = this.safeQuery(selector);
            if (element) {
                element[property] = value;
            }
        });
    }

    updateLayerProperty(property, value) {
        if (!this.selectedLayer) return;

        this.selectedLayer[property] = value;

        // Update display value
        const valueElement = this.safeQuery(`#${property}Value`);
        if (valueElement) {
            valueElement.textContent = value;
        }

        this.updateLayerList();
        this.render();
        this.callbacks.onPatternChanged();
    }

    updatePatternDuration(newDuration) {
        // Clamp duration to valid range
        newDuration = Math.max(100, Math.min(30000, newDuration));

        // Update pattern duration
        this.duration = newDuration;

        // Update the time display
        const timeDisplay = this.safeQuery('.time-display');
        if (timeDisplay) {
            timeDisplay.textContent = `${(this.currentTime / 1000).toFixed(1)}s / ${(this.duration / 1000).toFixed(1)}s`;
        }

        // Update layer duration sliders' max values
        const startTimeSlider = this.safeQuery('#startTimeSlider');
        const layerDurationSlider = this.safeQuery('#layerDurationSlider');

        if (startTimeSlider) {
            startTimeSlider.max = this.duration;
        }

        if (layerDurationSlider) {
            layerDurationSlider.max = this.duration;
            // If current layer duration exceeds new pattern duration, adjust it
            if (this.selectedLayer && this.selectedLayer.duration > this.duration) {
                this.selectedLayer.duration = this.duration;
                layerDurationSlider.value = this.duration;
                const durationValueElement = this.safeQuery('#layerDurationValue');
                if (durationValueElement) {
                    durationValueElement.textContent = this.duration;
                }
            }
        }

        // Adjust any layers that exceed the new duration
        this.layers.forEach(layer => {
            if (layer.startTime >= this.duration) {
                layer.startTime = Math.max(0, this.duration - 100);
            }
            if (layer.duration === 0) {
                // Duration of 0 means use pattern duration - no adjustment needed
            } else if (layer.startTime + layer.duration > this.duration) {
                layer.duration = Math.max(100, this.duration - layer.startTime);
            }
        });

        // Adjust control points if they exceed the new duration
        this.controlPoints = this.controlPoints.filter(point => {
            const timeMs = point.time * this.duration;
            return timeMs <= this.duration;
        });

        // Ensure we have control points at the start and end
        if (this.controlPoints.length === 0) {
            this.controlPoints = [
                { time: 0, intensity: 0.7 },
                { time: 1, intensity: 0.7 }
            ];
        } else {
            // Sort points and ensure first is at 0 and last is at 1
            this.controlPoints.sort((a, b) => a.time - b.time);
            if (this.controlPoints[0].time > 0) {
                this.controlPoints.unshift({ time: 0, intensity: this.controlPoints[0].intensity });
            }
            if (this.controlPoints[this.controlPoints.length - 1].time < 1) {
                this.controlPoints.push({ time: 1, intensity: this.controlPoints[this.controlPoints.length - 1].intensity });
            }
        }

        this.updateLayerList();
        this.render();
        this.callbacks.onPatternChanged();
    }

    // Canvas interaction methods
    handleCanvasMouseDown(e) {
        const rect = this.canvas.getBoundingClientRect();
        const x = e.clientX - rect.left;
        const y = e.clientY - rect.top;

        // Check for panning with middle mouse or space+left click
        if (e.button === 1 || (e.button === 0 && this.spacePressed)) {
            this.isPanning = true;
            this.lastPanX = x;
            this.lastPanY = y;
            this.canvas.style.cursor = 'move';
            e.preventDefault();
            return;
        }

        // Check if clicking on a control point
        const point = this.findControlPointAt(x, y);
        if (point) {
            this.selectedPoint = point;
            this.updateSelectedPointIndex();
            this.isDragging = true;
            this.canvas.style.cursor = 'grabbing';
            return;
        }

        // Add new control point if in add mode
        if (e.ctrlKey || this.container.querySelector('#addPointBtn').classList.contains('active')) {
            this.addControlPointAt(x, y);
        }
    }

    handleCanvasMouseMove(e) {
        const rect = this.canvas.getBoundingClientRect();
        const x = e.clientX - rect.left;
        const y = e.clientY - rect.top;

        if (this.isPanning) {
            const deltaX = x - this.lastPanX;
            const deltaY = y - this.lastPanY;
            this.panX += deltaX;
            this.panY = (this.panY || 0) + deltaY;

            // Apply viewport constraints
            this.constrainViewport();

            this.lastPanX = x;
            this.lastPanY = y;
            this.scheduleRender();
            return;
        }

        if (this.isDragging && this.selectedPoint) {
            this.moveControlPoint(this.selectedPoint, x, y);
            this.scheduleRender();
            this.callbacks.onPatternChanged();
            return;
        }

        // Update cursor based on what's under mouse
        const point = this.findControlPointAt(x, y);
        if (this.spacePressed) {
            this.canvas.style.cursor = 'move';
        } else {
            this.canvas.style.cursor = point ? 'grab' : 'default';
        }
    }

    handleCanvasMouseUp(e) {
        this.isDragging = false;
        this.isPanning = false;
        // Don't clear selectedPoint - keep it for keyboard navigation

        if (this.spacePressed) {
            this.canvas.style.cursor = 'move';
        } else {
            this.canvas.style.cursor = 'default';
        }
    }

    handleCanvasContextMenu(e) {
        e.preventDefault();

        const rect = this.canvas.getBoundingClientRect();
        const x = e.clientX - rect.left;
        const y = e.clientY - rect.top;

        const point = this.findControlPointAt(x, y);
        if (point) {
            this.removeControlPoint(point);
        }
    }

    handleCanvasWheel(e) {
        e.preventDefault();

        const zoomFactor = e.deltaY > 0 ? 0.9 : 1.1;
        const oldZoom = this.zoom;
        this.zoom *= zoomFactor;

        // Adjust pan to zoom towards mouse position
        const rect = this.canvas.getBoundingClientRect();
        const mouseX = e.clientX - rect.left;
        const zoomRatio = this.zoom / oldZoom;
        this.panX = (this.panX - mouseX) * zoomRatio + mouseX;

        // Apply constraints after zoom and pan changes
        this.constrainViewport();

        this.render();
    }

    handleKeyDown(e) {
        if (e.code === 'Space' && !this.spacePressed) {
            this.spacePressed = true;
            this.canvas.style.cursor = 'move';
            e.preventDefault();
        }
    }

    handleKeyUp(e) {
        if (e.code === 'Space') {
            this.spacePressed = false;
            this.canvas.style.cursor = 'default';
        }
    }

    handleCanvasKeyDown(e) {
        if (e.target !== this.canvas) return;

        const step = 10;
        const zoomStep = 0.1;

        switch (e.code) {
            case 'ArrowLeft':
                this.panX += step;
                this.constrainViewport();
                this.scheduleRender();
                e.preventDefault();
                break;
            case 'ArrowRight':
                this.panX -= step;
                this.constrainViewport();
                this.scheduleRender();
                e.preventDefault();
                break;
            case 'ArrowUp':
                if (this.selectedPoint) {
                    this.selectedPoint.intensity = Math.min(100, this.selectedPoint.intensity + 5);
                    this.controlPoints.sort((a, b) => a.time - b.time);
                    this.updateSelectedPointIndex();
                    this.scheduleRender();
                    this.callbacks.onPatternChanged();
                } else {
                    this.panY = (this.panY || 0) + step;
                    this.constrainViewport();
                    this.scheduleRender();
                }
                e.preventDefault();
                break;
            case 'ArrowDown':
                if (this.selectedPoint) {
                    this.selectedPoint.intensity = Math.max(0, this.selectedPoint.intensity - 5);
                    this.controlPoints.sort((a, b) => a.time - b.time);
                    this.updateSelectedPointIndex();
                    this.scheduleRender();
                    this.callbacks.onPatternChanged();
                } else {
                    this.panY = (this.panY || 0) - step;
                    this.constrainViewport();
                    this.scheduleRender();
                }
                e.preventDefault();
                break;
            case 'Equal':
            case 'NumpadAdd':
                if (e.shiftKey || e.ctrlKey) {
                    this.zoom = Math.min(5, this.zoom + zoomStep);
                    this.constrainViewport();
                    this.scheduleRender();
                    e.preventDefault();
                }
                break;
            case 'Minus':
            case 'NumpadSubtract':
                if (e.shiftKey || e.ctrlKey) {
                    this.zoom = Math.max(0.1, this.zoom - zoomStep);
                    this.constrainViewport();
                    this.scheduleRender();
                    e.preventDefault();
                }
                break;
            case 'Digit0':
            case 'Numpad0':
                if (e.ctrlKey) {
                    this.zoom = 1;
                    this.panX = 0;
                    this.panY = 0;
                    this.constrainViewport();
                    this.scheduleRender();
                    e.preventDefault();
                }
                break;
            case 'Delete':
            case 'Backspace':
                if (this.selectedPoint && this.controlPoints.length > 2) {
                    // Remove the selected point and select the nearest remaining point
                    const sortedPoints = this.getSortedPoints();
                    const currentIndex = sortedPoints.findIndex(p => p === this.selectedPoint);

                    this.removeControlPoint(this.selectedPoint);

                    // Select the nearest remaining point
                    const remainingPoints = this.getSortedPoints();
                    if (remainingPoints.length > 0) {
                        const newIndex = Math.min(currentIndex, remainingPoints.length - 1);
                        this.selectControlPointByIndex(newIndex);
                    } else {
                        this.selectedPoint = null;
                        this.selectedPointIndex = -1;
                    }

                    e.preventDefault();
                }
                break;
            case 'Enter':
                // Toggle selection of the nearest point to playback cursor or viewport center
                if (this.controlPoints.length > 0) {
                    let targetTime;
                    if (this.isPlaying) {
                        // Select nearest to playback cursor
                        targetTime = this.currentTime;
                    } else {
                        // Select nearest to center of viewport
                        targetTime = this.xToTime(this.logicalWidth / 2);
                    }

                    // Find nearest control point to target time
                    const sortedPoints = this.getSortedPoints();
                    let nearestIndex = 0;
                    let minDistance = Math.abs(sortedPoints[0].time - targetTime);

                    for (let i = 1; i < sortedPoints.length; i++) {
                        const distance = Math.abs(sortedPoints[i].time - targetTime);
                        if (distance < minDistance) {
                            minDistance = distance;
                            nearestIndex = i;
                        }
                    }

                    this.selectControlPointByIndex(nearestIndex);
                    e.preventDefault();
                }
                break;
            case 'Tab':
                // Navigate between control points (prevent default to keep focus on canvas)
                if (this.controlPoints.length > 0) {
                    if (e.shiftKey) {
                        this.selectPrevPoint();
                    } else {
                        this.selectNextPoint();
                    }
                    e.preventDefault();
                }
                break;
            case 'BracketLeft': // [ key - alternative shortcut for previous
                if (this.controlPoints.length > 0) {
                    this.selectPrevPoint();
                    e.preventDefault();
                }
                break;
            case 'BracketRight': // ] key - alternative shortcut for next
                if (this.controlPoints.length > 0) {
                    this.selectNextPoint();
                    e.preventDefault();
                }
                break;
        }

        // Announce changes to screen readers
        if (this.selectedPoint && (e.code.startsWith('Arrow') || e.code === 'Delete' || e.code === 'Backspace')) {
            const timeDisplay = this.safeQuery('.time-display');
            if (timeDisplay) {
                timeDisplay.setAttribute('aria-live', 'assertive');
                timeDisplay.textContent = `Control point at ${(this.selectedPoint.time / 1000).toFixed(1)}s, intensity ${this.selectedPoint.intensity.toFixed(0)}%`;
                setTimeout(() => timeDisplay.setAttribute('aria-live', 'polite'), 100);
            }
        }
    }

    handleResize() {
        if (!this.canvas) return;

        const wrapper = this.safeQuery('.timeline-canvas-wrapper');
        const rect = this.canvas.getBoundingClientRect();

        // Update logical dimensions
        this.logicalWidth = rect.width || (wrapper ? wrapper.clientWidth - 20 : 800);
        this.logicalHeight = rect.height || 300;

        // Update canvas backing store with DPR
        const dpr = window.devicePixelRatio || 1;
        this.canvas.width = this.logicalWidth * dpr;
        this.canvas.height = this.logicalHeight * dpr;
        this.canvas.style.width = this.logicalWidth + 'px';
        this.canvas.style.height = this.logicalHeight + 'px';
        this.ctx.scale(dpr, dpr);

        this.render();
    }

    // Control point management
    findControlPointAt(x, y) {
        const tolerance = 10;
        return this.controlPoints.find(point => {
            const pointX = this.timeToX(point.time);
            const pointY = this.intensityToY(point.intensity);
            return Math.abs(x - pointX) <= tolerance && Math.abs(y - pointY) <= tolerance;
        });
    }

    addControlPointAt(x, y) {
        const time = this.xToTime(x);
        const intensity = this.yToIntensity(y);

        if (time < 0 || time > this.duration || intensity < 0 || intensity > 100) return;

        const point = { time, intensity, curveType: 'Linear' };
        this.controlPoints.push(point);
        this.controlPoints.sort((a, b) => a.time - b.time);

        // Update selected point index if we have a selection
        this.updateSelectedPointIndex();

        this.render();
        this.callbacks.onPatternChanged();
    }

    addControlPoint() {
        // Add point at center of timeline
        this.addControlPointAt(this.canvas.width / 2, this.canvas.height / 2);
    }

    removeControlPoint(point) {
        const index = this.controlPoints.indexOf(point);
        if (index !== -1) {
            this.controlPoints.splice(index, 1);

            // Update selected point index if the removed point was selected
            if (point === this.selectedPoint) {
                this.selectedPoint = null;
                this.selectedPointIndex = -1;
            } else {
                this.updateSelectedPointIndex();
            }

            this.render();
            this.callbacks.onPatternChanged();
        }
    }

    moveControlPoint(point, x, y) {
        point.time = Math.max(0, Math.min(this.duration, this.xToTime(x)));
        point.intensity = Math.max(0, Math.min(100, this.yToIntensity(y)));

        // Re-sort points by time
        this.controlPoints.sort((a, b) => a.time - b.time);

        // Update selected point index after sorting
        this.updateSelectedPointIndex();
    }

    // Coordinate conversion methods with bounds checking
    timeToX(time) {
        if (this.duration <= 0 || this.logicalWidth <= 60) return 30;
        const normalizedTime = Math.max(0, Math.min(this.duration, time));
        const zoom = Math.max(0.1, Math.min(5, this.zoom));
        return 30 + ((normalizedTime / this.duration) * (this.logicalWidth - 60) * zoom) + this.panX;
    }

    xToTime(x) {
        if (this.duration <= 0 || this.logicalWidth <= 60 || this.zoom <= 0) return 0;
        const zoom = Math.max(0.1, Math.min(5, this.zoom));
        const time = ((x - 30 - this.panX) / ((this.logicalWidth - 60) * zoom)) * this.duration;
        return Math.max(0, Math.min(this.duration, time));
    }

    intensityToY(intensity) {
        if (this.logicalHeight <= 60) return this.logicalHeight / 2;
        const normalizedIntensity = Math.max(0, Math.min(100, intensity));
        return this.logicalHeight - 30 - ((normalizedIntensity / 100) * (this.logicalHeight - 60));
    }

    yToIntensity(y) {
        if (this.logicalHeight <= 60) return 50;
        const intensity = ((this.logicalHeight - 30 - y) / (this.logicalHeight - 60)) * 100;
        return Math.max(0, Math.min(100, intensity));
    }

    // Constrain pan and zoom values to reasonable bounds
    constrainViewport() {
        // Constrain zoom
        this.zoom = Math.max(0.1, Math.min(5, this.zoom));

        // Constrain pan to prevent getting lost
        const maxPanX = (this.logicalWidth - 60) * (this.zoom - 1);
        const maxPanY = (this.logicalHeight - 60) * 0.5;

        this.panX = Math.max(-maxPanX, Math.min(maxPanX, this.panX));
        this.panY = Math.max(-maxPanY, Math.min(maxPanY, this.panY || 0));
    }

    // Control point selection and traversal helpers
    getSortedPoints() {
        return [...this.controlPoints].sort((a, b) => a.time - b.time);
    }

    selectControlPointByIndex(index) {
        const sortedPoints = this.getSortedPoints();
        if (index >= 0 && index < sortedPoints.length) {
            this.selectedPoint = sortedPoints[index];
            this.selectedPointIndex = index;
            this.announceSelectedPoint();
            this.scheduleRender();
        }
    }

    selectNextPoint() {
        const sortedPoints = this.getSortedPoints();
        if (sortedPoints.length === 0) return;

        if (this.selectedPointIndex === -1) {
            this.selectControlPointByIndex(0);
        } else {
            const nextIndex = (this.selectedPointIndex + 1) % sortedPoints.length;
            this.selectControlPointByIndex(nextIndex);
        }
    }

    selectPrevPoint() {
        const sortedPoints = this.getSortedPoints();
        if (sortedPoints.length === 0) return;

        if (this.selectedPointIndex === -1) {
            this.selectControlPointByIndex(sortedPoints.length - 1);
        } else {
            const prevIndex = this.selectedPointIndex <= 0 ? sortedPoints.length - 1 : this.selectedPointIndex - 1;
            this.selectControlPointByIndex(prevIndex);
        }
    }

    announceSelectedPoint() {
        if (!this.selectedPoint) return;

        const ariaLiveElement = this.safeQuery('.sr-only-announce');
        if (ariaLiveElement) {
            const sortedPoints = this.getSortedPoints();
            const pointIndex = sortedPoints.findIndex(p => p === this.selectedPoint);
            const text = `Selected point ${pointIndex + 1}/${sortedPoints.length} at ${this.selectedPoint.time} ms, ${this.selectedPoint.intensity.toFixed(0)}%`;
            ariaLiveElement.textContent = text;
        }
    }

    updateSelectedPointIndex() {
        if (!this.selectedPoint) {
            this.selectedPointIndex = -1;
            return;
        }

        const sortedPoints = this.getSortedPoints();
        this.selectedPointIndex = sortedPoints.findIndex(p => p === this.selectedPoint);
    }

    // Schedule render with RAF throttling
    scheduleRender() {
        if (!this.renderScheduled) {
            this.renderScheduled = true;
            requestAnimationFrame(() => {
                this.render();
                this.renderScheduled = false;
            });
        }
    }

    // Rendering methods
    render() {
        if (!this.ctx || !this.canvas) return;

        this.ctx.clearRect(0, 0, this.logicalWidth, this.logicalHeight);

        this.drawGrid();
        this.drawCompositeWaveform();
        this.drawLayers();
        this.drawIntensityCurve();
        this.drawControlPoints();
        this.drawPlaybackCursor();
        this.drawLabels();
    }

    drawGrid() {
        this.ctx.strokeStyle = 'rgba(255, 255, 255, 0.1)';
        this.ctx.lineWidth = 1;

        // Time grid lines
        for (let time = 0; time <= this.duration; time += this.gridTimeStep) {
            const x = this.timeToX(time);
            if (x >= 30 && x <= this.logicalWidth - 30) {
                this.ctx.beginPath();
                this.ctx.moveTo(x, 0);
                this.ctx.lineTo(x, this.logicalHeight);
                this.ctx.stroke();
            }
        }

        // Intensity grid lines
        for (let intensity = 0; intensity <= 100; intensity += this.gridIntensityStep) {
            const y = this.intensityToY(intensity);
            this.ctx.beginPath();
            this.ctx.moveTo(0, y);
            this.ctx.lineTo(this.logicalWidth, y);
            this.ctx.stroke();
        }
    }

    drawLayers() {
        this.layers.forEach((layer, index) => {
            if (!layer.visible) return;

            this.ctx.strokeStyle = layer.color;
            this.ctx.lineWidth = 2;
            this.ctx.globalAlpha = layer.muted ? 0.3 : 0.6;

            const startX = this.timeToX(layer.startTime);
            const endX = this.timeToX(layer.startTime + layer.duration);
            const samples = Math.floor((endX - startX) / 2);

            this.ctx.beginPath();

            for (let i = 0; i < samples; i++) {
                const x = startX + (i / samples) * (endX - startX);
                const time = this.xToTime(x) - layer.startTime;

                if (time < 0 || time > layer.duration) continue;

                const phase = (time / 1000) * layer.frequency * 2 * Math.PI + (layer.phase * Math.PI / 180);
                let waveValue = 0;

                switch (layer.waveform) {
                    case 'Sine':
                        waveValue = Math.sin(phase);
                        break;
                    case 'Square':
                        waveValue = Math.sin(phase) > 0 ? 1 : -1;
                        break;
                    case 'Triangle':
                        waveValue = (2 / Math.PI) * Math.asin(Math.sin(phase));
                        break;
                    case 'Sawtooth':
                        waveValue = (2 / Math.PI) * Math.atan(Math.tan(phase / 2));
                        break;
                    case 'Noise':
                        waveValue = Math.random() * 2 - 1;
                        break;
                }

                // Apply fade in/out with zero division protection
                let envelope = 1;
                if (layer.fadeIn > 0 && time < layer.fadeIn) {
                    envelope = time / layer.fadeIn;
                } else if (layer.fadeOut > 0 && time > layer.duration - layer.fadeOut) {
                    envelope = (layer.duration - time) / layer.fadeOut;
                }

                const intensity = (layer.amplitude / 100) * envelope * ((waveValue + 1) / 2) * 100;
                const y = this.intensityToY(intensity);

                if (i === 0) {
                    this.ctx.moveTo(x, y);
                } else {
                    this.ctx.lineTo(x, y);
                }
            }

            this.ctx.stroke();
            this.ctx.globalAlpha = 1;
        });
    }

    drawCompositeWaveform() {
        if (this.layers.length === 0) return;

        const visibleLayers = this.layers.filter(layer => layer.visible && !layer.muted);
        if (visibleLayers.length === 0) return;

        this.ctx.strokeStyle = 'rgba(255, 255, 255, 0.15)';
        this.ctx.fillStyle = 'rgba(255, 255, 255, 0.05)';
        this.ctx.lineWidth = 1;

        const startX = this.timeToX(0);
        const endX = this.timeToX(this.duration);
        const samples = Math.floor((endX - startX) / 2);

        this.ctx.beginPath();
        this.ctx.moveTo(startX, this.intensityToY(0));

        for (let i = 0; i < samples; i++) {
            const x = startX + (i / samples) * (endX - startX);
            const time = this.xToTime(x);

            let totalIntensity = 0;
            let activeLayerCount = 0;

            visibleLayers.forEach(layer => {
                const layerTime = time - layer.startTime;
                if (layerTime < 0 || layerTime > layer.duration) return;

                const phase = (layerTime / 1000) * layer.frequency * 2 * Math.PI + (layer.phase * Math.PI / 180);
                let waveValue = 0;

                switch (layer.waveform) {
                    case 'Sine':
                        waveValue = Math.sin(phase);
                        break;
                    case 'Square':
                        waveValue = Math.sin(phase) > 0 ? 1 : -1;
                        break;
                    case 'Triangle':
                        waveValue = (2 / Math.PI) * Math.asin(Math.sin(phase));
                        break;
                    case 'Sawtooth':
                        waveValue = (2 / Math.PI) * Math.atan(Math.tan(phase / 2));
                        break;
                    case 'Noise':
                        waveValue = Math.random() * 2 - 1;
                        break;
                }

                // Apply fade in/out with zero division protection
                let envelope = 1;
                if (layer.fadeIn > 0 && layerTime < layer.fadeIn) {
                    envelope = layerTime / layer.fadeIn;
                } else if (layer.fadeOut > 0 && layerTime > layer.duration - layer.fadeOut) {
                    envelope = (layer.duration - layerTime) / layer.fadeOut;
                }

                const layerIntensity = (layer.amplitude / 100) * envelope * ((waveValue + 1) / 2) * 100;
                totalIntensity += layerIntensity;
                activeLayerCount++;
            });

            // Average the intensity across active layers to prevent overflow
            const compositeIntensity = activeLayerCount > 0 ? Math.min(100, totalIntensity / activeLayerCount) : 0;
            const y = this.intensityToY(compositeIntensity);

            this.ctx.lineTo(x, y);
        }

        // Complete the filled area
        this.ctx.lineTo(endX, this.intensityToY(0));
        this.ctx.lineTo(startX, this.intensityToY(0));
        this.ctx.closePath();

        // Fill the composite waveform area
        this.ctx.fill();

        // Stroke the composite waveform outline
        this.ctx.beginPath();
        this.ctx.moveTo(startX, this.intensityToY(0));

        for (let i = 0; i < samples; i++) {
            const x = startX + (i / samples) * (endX - startX);
            const time = this.xToTime(x);

            let totalIntensity = 0;
            let activeLayerCount = 0;

            visibleLayers.forEach(layer => {
                const layerTime = time - layer.startTime;
                if (layerTime < 0 || layerTime > layer.duration) return;

                const phase = (layerTime / 1000) * layer.frequency * 2 * Math.PI + (layer.phase * Math.PI / 180);
                let waveValue = 0;

                switch (layer.waveform) {
                    case 'Sine':
                        waveValue = Math.sin(phase);
                        break;
                    case 'Square':
                        waveValue = Math.sin(phase) > 0 ? 1 : -1;
                        break;
                    case 'Triangle':
                        waveValue = (2 / Math.PI) * Math.asin(Math.sin(phase));
                        break;
                    case 'Sawtooth':
                        waveValue = (2 / Math.PI) * Math.atan(Math.tan(phase / 2));
                        break;
                    case 'Noise':
                        waveValue = Math.random() * 2 - 1;
                        break;
                }

                let envelope = 1;
                if (layer.fadeIn > 0 && layerTime < layer.fadeIn) {
                    envelope = layerTime / layer.fadeIn;
                } else if (layer.fadeOut > 0 && layerTime > layer.duration - layer.fadeOut) {
                    envelope = (layer.duration - layerTime) / layer.fadeOut;
                }

                const layerIntensity = (layer.amplitude / 100) * envelope * ((waveValue + 1) / 2) * 100;
                totalIntensity += layerIntensity;
                activeLayerCount++;
            });

            const compositeIntensity = activeLayerCount > 0 ? Math.min(100, totalIntensity / activeLayerCount) : 0;
            const y = this.intensityToY(compositeIntensity);

            if (i === 0) {
                this.ctx.moveTo(x, y);
            } else {
                this.ctx.lineTo(x, y);
            }
        }

        this.ctx.stroke();
    }

    drawIntensityCurve() {
        if (this.controlPoints.length < 2) return;

        this.ctx.strokeStyle = this.colors.accent;
        this.ctx.lineWidth = 3;
        this.ctx.beginPath();

        const samples = this.logicalWidth - 60;
        for (let i = 0; i < samples; i++) {
            const x = 30 + i;
            const time = this.xToTime(x);
            const intensity = this.interpolateIntensity(time);
            const y = this.intensityToY(intensity);

            if (i === 0) {
                this.ctx.moveTo(x, y);
            } else {
                this.ctx.lineTo(x, y);
            }
        }

        this.ctx.stroke();
    }

    drawControlPoints() {
        this.controlPoints.forEach(point => {
            const x = this.timeToX(point.time);
            const y = this.intensityToY(point.intensity);

            this.ctx.fillStyle = this.colors.primary;
            this.ctx.strokeStyle = '#fff';
            this.ctx.lineWidth = 2;

            this.ctx.beginPath();
            this.ctx.arc(x, y, 6, 0, Math.PI * 2);
            this.ctx.fill();
            this.ctx.stroke();

            if (point === this.selectedPoint) {
                // Draw high-contrast focus ring for keyboard navigation
                this.ctx.strokeStyle = this.colors.accent;
                this.ctx.lineWidth = 2;
                this.ctx.beginPath();
                this.ctx.arc(x, y, 10, 0, Math.PI * 2);
                this.ctx.stroke();

                // Draw inner selection ring
                this.ctx.strokeStyle = this.colors.accent;
                this.ctx.lineWidth = 3;
                this.ctx.beginPath();
                this.ctx.arc(x, y, 9, 0, Math.PI * 2);
                this.ctx.stroke();
            }
        });
    }

    drawPlaybackCursor() {
        if (!this.isPlaying) return;

        const x = this.timeToX(this.currentTime);
        this.ctx.strokeStyle = this.colors.timelineCursor;
        this.ctx.lineWidth = 2;

        this.ctx.beginPath();
        this.ctx.moveTo(x, 0);
        this.ctx.lineTo(x, this.canvas.height);
        this.ctx.stroke();
    }

    drawLabels() {
        this.ctx.fillStyle = '#fff';
        this.ctx.font = '12px Arial';
        this.ctx.textAlign = 'center';

        // Time labels
        for (let time = 0; time <= this.duration; time += this.gridTimeStep * 2) {
            const x = this.timeToX(time);
            const label = (time / 1000).toFixed(1) + 's';
            this.ctx.fillText(label, x, this.logicalHeight - 5);
        }

        // Intensity labels
        this.ctx.textAlign = 'right';
        for (let intensity = 0; intensity <= 100; intensity += this.gridIntensityStep * 2) {
            const y = this.intensityToY(intensity);
            this.ctx.fillText(intensity + '%', 25, y + 4);
        }
    }

    interpolateIntensity(time) {
        if (this.controlPoints.length === 0) return 50;
        if (this.controlPoints.length === 1) return this.controlPoints[0].intensity;

        // Find surrounding points
        let leftPoint = this.controlPoints[0];
        let rightPoint = this.controlPoints[this.controlPoints.length - 1];

        for (let i = 0; i < this.controlPoints.length - 1; i++) {
            if (time >= this.controlPoints[i].time && time <= this.controlPoints[i + 1].time) {
                leftPoint = this.controlPoints[i];
                rightPoint = this.controlPoints[i + 1];
                break;
            }
        }

        if (time <= leftPoint.time) return leftPoint.intensity;
        if (time >= rightPoint.time) return rightPoint.intensity;

        // Interpolate based on curve type
        const t = (time - leftPoint.time) / (rightPoint.time - leftPoint.time);
        const curveType = leftPoint.curveType || 'Linear';

        let easedT = t;
        switch (curveType) {
            case 'Exponential':
                easedT = Math.pow(t, 2);
                break;
            case 'Logarithmic':
                easedT = Math.sqrt(t);
                break;
            case 'Sine':
                easedT = 0.5 * (1 - Math.cos(t * Math.PI));
                break;
            case 'Bounce':
                easedT = this.bounceEase(t);
                break;
        }

        return leftPoint.intensity + (rightPoint.intensity - leftPoint.intensity) * easedT;
    }

    bounceEase(t) {
        if (t < 1 / 2.75) {
            return 7.5625 * t * t;
        } else if (t < 2 / 2.75) {
            return 7.5625 * (t -= 1.5 / 2.75) * t + 0.75;
        } else if (t < 2.5 / 2.75) {
            return 7.5625 * (t -= 2.25 / 2.75) * t + 0.9375;
        } else {
            return 7.5625 * (t -= 2.625 / 2.75) * t + 0.984375;
        }
    }

    updateCurveType(curveType) {
        // Set global curve type for pattern instead of per-point
        this.globalCurveType = curveType;
        this.render();
        this.callbacks.onPatternChanged();
    }

    // Playback controls
    play() {
        this.isPlaying = true;
        this.currentTime = 0;
        this.container.querySelector('#playBtn').style.display = 'none';
        this.container.querySelector('#pauseBtn').style.display = 'inline-block';

        this.playbackInterval = setInterval(() => {
            this.currentTime += 50;
            if (this.currentTime >= this.duration) {
                this.stop();
                return;
            }

            const timeDisplay = this.container.querySelector('.time-display');
            timeDisplay.textContent = `${(this.currentTime / 1000).toFixed(1)}s / ${(this.duration / 1000).toFixed(1)}s`;

            this.render();
        }, 50);
    }

    pause() {
        this.isPlaying = false;
        if (this.playbackInterval) {
            clearInterval(this.playbackInterval);
        }
        this.container.querySelector('#playBtn').style.display = 'inline-block';
        this.container.querySelector('#pauseBtn').style.display = 'none';
    }

    stop() {
        this.isPlaying = false;
        this.currentTime = 0;
        if (this.playbackInterval) {
            clearInterval(this.playbackInterval);
        }
        this.container.querySelector('#playBtn').style.display = 'inline-block';
        this.container.querySelector('#pauseBtn').style.display = 'none';

        const timeDisplay = this.container.querySelector('.time-display');
        timeDisplay.textContent = `0.0s / ${(this.duration / 1000).toFixed(1)}s`;

        this.render();
    }

    // Zoom controls
    zoomIn() {
        this.zoom *= 1.2;
        this.constrainViewport();
        this.render();
    }

    zoomOut() {
        this.zoom *= 0.8;
        this.constrainViewport();
        this.render();
    }

    resetZoom() {
        this.zoom = 1.0;
        this.panX = 0;
        this.panY = 0;
        this.constrainViewport();
        this.render();
    }

    // Pattern data methods
    getHapticPattern() {
        const pattern = {
            Name: "Timeline Pattern", // Proper HapticPattern.Name casing
            Duration: this.duration,
            Frequency: 40, // Default base frequency
            Intensity: 100, // Default base intensity
            FadeIn: 0,
            FadeOut: 0,
            IntensityCurve: this.controlPoints.length > 0 ? this.globalCurveType : "Linear",
            Layers: this.layers.map(layer => ({
                Waveform: layer.waveform,
                Frequency: layer.frequency,
                Amplitude: layer.amplitude / 100,
                PhaseOffset: layer.phase,
                StartTime: layer.startTime,
                Duration: layer.duration,
                FadeIn: layer.fadeIn,
                FadeOut: layer.fadeOut,
                Curve: layer.curve || "Linear"
            })),
            CustomCurvePoints: [...this.controlPoints].sort((a, b) => a.time - b.time).map(point => ({
                Time: point.time / this.duration,
                Intensity: point.intensity / 100
            }))
        };

        return pattern;
    }

    setHapticPattern(pattern) {
        // Clear existing data
        this.layers = [];
        this.controlPoints = [];

        // Set basic properties
        this.duration = pattern.Duration || 3000;
        this.globalCurveType = pattern.IntensityCurve || 'Linear';

        // Load layers
        if (pattern.Layers && pattern.Layers.length > 0) {
            pattern.Layers.forEach((layerData, index) => {
                const layer = {
                    id: Date.now() + index,
                    waveform: layerData.Waveform || 'Sine',
                    frequency: layerData.Frequency || 40,
                    amplitude: (layerData.Amplitude || 1) * 100,
                    phase: layerData.PhaseOffset || 0,
                    startTime: layerData.StartTime || 0,
                    duration: layerData.Duration || this.duration,
                    fadeIn: layerData.FadeIn || 0,
                    fadeOut: layerData.FadeOut || 0,
                    color: this.layerColors[index % this.layerColors.length],
                    visible: true,
                    muted: false
                };
                this.layers.push(layer);
            });
        } else {
            // Create default layer from basic pattern properties
            this.addLayer({
                frequency: pattern.Frequency || 40,
                amplitude: pattern.Intensity || 100,
                fadeIn: pattern.FadeIn || 0,
                fadeOut: pattern.FadeOut || 0
            });
        }

        // Load custom curve points
        if (pattern.CustomCurvePoints && pattern.CustomCurvePoints.length > 0) {
            this.controlPoints = [...pattern.CustomCurvePoints]
                .sort((a, b) => a.Time - b.Time)
                .map(point => ({
                    time: point.Time * this.duration,
                    intensity: point.Intensity * 100,
                    curveType: 'Linear'
                }));
        } else {
            // Create default curve points
            this.controlPoints = [
                { time: 0, intensity: pattern.Intensity || 100, curveType: 'Linear' },
                { time: this.duration, intensity: pattern.Intensity || 100, curveType: 'Linear' }
            ];
        }

        // Update UI
        this.updateLayerList();
        if (this.layers.length > 0) {
            this.selectLayer(this.layers[0]);
        }

        // Update duration input field
        const durationInput = this.safeQuery('#durationInput');
        if (durationInput) {
            durationInput.value = this.duration;
        }

        // Update time display
        const timeDisplay = this.safeQuery('.time-display');
        if (timeDisplay) {
            timeDisplay.textContent = `${(this.currentTime / 1000).toFixed(1)}s / ${(this.duration / 1000).toFixed(1)}s`;
        }

        this.render();
    }

    // Event callback registration
    on(event, callback) {
        if (this.callbacks[event]) {
            this.callbacks[event] = callback;
        }
    }
}

// Export for use in other modules
window.TimelineEditor = TimelineEditor;