// Pattern source names, pack names and authors come from files this application did not write, so
// every one of them is rendered as a text node via dom.el/dom.replace rather than concatenated into
// markup. See js/dom.js.
const { el, replace, icon, slug, num } = window.dom;

class PatternConflictsManager {
    constructor() {
        this.conflictData = null;
        this.stats = null;
        this.selectedResolutionStrategy = null;
        
        this.initializeEventHandlers();
        this.loadConflicts();
        this.loadStats();
    }

    initializeEventHandlers() {
        // Auto-resolve strategy selection
        document.getElementById('resolveOptions').addEventListener('click', (e) => {
            const option = e.target.closest('.resolve-option');
            if (option) {
                this.selectResolutionStrategy(option);
            }
        });

        // Auto-resolve button
        document.getElementById('autoResolveBtn').addEventListener('click', () => {
            this.autoResolveConflicts();
        });

        // Pattern selection handlers will be added dynamically
    }

    async loadConflicts() {
        try {
            const response = await fetch('/api/patternselection/conflicts');
            if (!response.ok) throw new Error('Failed to load conflicts');
            
            this.conflictData = await response.json();
            this.renderConflicts();
            this.updateConflictCount();
        } catch (error) {
            console.error('Error loading conflicts:', error);
            this.showError('Failed to load pattern conflicts');
        }
    }

    async loadStats() {
        try {
            const response = await fetch('/api/patternselection/stats');
            if (!response.ok) throw new Error('Failed to load stats');
            
            this.stats = await response.json();
            this.renderStats();
        } catch (error) {
            console.error('Error loading stats:', error);
            this.showError('Failed to load statistics');
        }
    }

    renderStats() {
        if (!this.stats) return;

        const rows = [
            ['Total Ship/Event Combinations', this.stats.totalShipEventCombinations],
            ['Available Patterns', this.stats.totalAvailablePatterns],
            ['Conflicting Combinations', this.stats.conflictingCombinations],
            ['File System Patterns', this.stats.fileSystemPatterns],
            ['User Custom Patterns', this.stats.userCustomPatterns],
            ['Default Patterns', this.stats.defaultPatterns]
        ];

        replace(document.getElementById('statsCard'), rows.map(([label, value]) => el('div', { className: 'stat-item' }, [
            el('span', { className: 'stat-label', text: label }),
            el('span', { className: 'stat-value', text: num(value) })
        ])));
    }

    renderConflicts() {
        const conflictsList = document.getElementById('conflictsList');
        
        if (!this.conflictData || this.conflictData.conflicts.length === 0) {
            replace(conflictsList, el('div', { className: 'no-conflicts' }, [
                icon('fa-check-circle'),
                el('h3', { text: 'No Pattern Conflicts' }),
                el('p', { text: 'All ship/event combinations have been resolved or only have one pattern available.' })
            ]));
            return;
        }

        replace(conflictsList, this.conflictData.conflicts.map(conflict => this.renderConflictCard(conflict)));
    }

    renderConflictCard(conflict) {
        const activePatternId = conflict.activePattern?.sourceId || '';
        const groupName = `pattern_${conflict.shipType}_${conflict.eventName}`;

        const card = el('div', {
            className: 'conflict-card has-conflicts',
            dataset: { 'ship-type': conflict.shipType, event: conflict.eventName }
        }, [
            el('div', { className: 'conflict-header' }, [
                el('div', { className: 'conflict-title' }, [conflict.shipType, ' - ', conflict.eventName]),
                el('div', { className: 'conflict-subtitle' },
                    el('span', { className: 'conflict-badge' }, [num(conflict.conflictCount), ' patterns available']))
            ]),
            el('div', { className: 'conflict-body' },
                el('div', { className: 'pattern-options' },
                    (conflict.availablePatterns || []).map(pattern =>
                        this.renderPatternOption(pattern, groupName, activePatternId))))
        ]);

        return card;
    }

    renderPatternOption(pattern, groupName, activePatternId) {
        const isActive = pattern.sourceId === activePatternId;

        const radio = el('input', {
            className: 'pattern-radio',
            attrs: { type: 'radio', name: groupName, value: pattern.sourceId }
        });
        radio.checked = isActive;

        const option = el('div', {
            className: 'pattern-option' + (isActive ? ' active' : ''),
            dataset: { 'source-id': pattern.sourceId }
        }, [
            radio,
            el('div', { className: 'pattern-info' }, [
                el('div', { className: 'pattern-name', text: pattern.sourceName }),
                el('div', { className: 'pattern-details' }, [
                    el('span', {}, ['Type: ', pattern.patternType]),
                    el('span', {}, ['Freq: ', num(pattern.frequency), 'Hz']),
                    el('span', {}, ['Int: ', num(pattern.intensity), '%']),
                    el('span', {}, ['Dur: ', num(pattern.duration), 'ms'])
                ]),
                el('div', { className: 'pattern-meta' }, [
                    el('span', {
                        className: 'source-type-badge source-type-' + slug(pattern.sourceType),
                        text: pattern.sourceType
                    }),
                    pattern.packName && el('span', {}, ['Pack: ', pattern.packName]),
                    pattern.author && el('span', {}, ['Author: ', pattern.author]),
                    pattern.version && el('span', {}, ['v', pattern.version])
                ])
            ])
        ]);

        option.addEventListener('click', e => {
            if (e.target === radio) return; // Let the radio's own change event handle it.
            radio.checked = true;
            this.selectPattern(option);
        });

        radio.addEventListener('change', () => {
            if (radio.checked) this.selectPattern(option);
        });

        return option;
    }

    async selectPattern(optionElement) {
        try {
            const conflictCard = optionElement.closest('.conflict-card');
            const shipType = conflictCard.dataset.shipType;
            const eventName = conflictCard.dataset.event;
            const sourceId = optionElement.dataset.sourceId;

            const response = await fetch('/api/patternselection/select', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({
                    shipType: shipType,
                    eventName: eventName,
                    sourceId: sourceId
                })
            });

            if (!response.ok) throw new Error('Failed to select pattern');

            const result = await response.json();
            
            // Update UI to reflect the change
            this.updatePatternSelection(conflictCard, sourceId);
            this.showSuccess(`Selected pattern: ${result.selectedPattern?.sourceName || 'Unknown'}`);
            
            // Reload conflicts to get updated data
            setTimeout(() => this.loadConflicts(), 500);
            
        } catch (error) {
            console.error('Error selecting pattern:', error);
            this.showError('Failed to select pattern');
        }
    }

    updatePatternSelection(conflictCard, selectedSourceId) {
        // Update active states
        conflictCard.querySelectorAll('.pattern-option').forEach(option => {
            if (option.dataset.sourceId === selectedSourceId) {
                option.classList.add('active');
            } else {
                option.classList.remove('active');
            }
        });
    }

    selectResolutionStrategy(optionElement) {
        // Clear previous selections
        document.querySelectorAll('.resolve-option').forEach(opt => {
            opt.classList.remove('selected');
        });
        
        // Select the clicked option
        optionElement.classList.add('selected');
        this.selectedResolutionStrategy = optionElement.dataset.strategy;
        
        // Enable the auto-resolve button
        const autoResolveBtn = document.getElementById('autoResolveBtn');
        autoResolveBtn.disabled = false;
        autoResolveBtn.textContent = `Auto-Resolve (${optionElement.querySelector('strong').textContent})`;
    }

    async autoResolveConflicts() {
        if (!this.selectedResolutionStrategy) {
            this.showError('Please select a resolution strategy first');
            return;
        }

        try {
            const autoResolveBtn = document.getElementById('autoResolveBtn');
            autoResolveBtn.disabled = true;
            replace(autoResolveBtn, [icon('fa-spinner fa-spin'), ' Resolving...']);

            const response = await fetch('/api/patternselection/auto-resolve', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({
                    resolutionStrategy: this.selectedResolutionStrategy
                })
            });

            if (!response.ok) throw new Error('Auto-resolve failed');

            const result = await response.json();
            
            this.showSuccess(`Resolved ${result.resolvedCount} of ${result.totalConflicts} conflicts using ${result.resolutionStrategy} strategy`);
            
            // Reload conflicts and stats
            await this.loadConflicts();
            await this.loadStats();
            
            // Reset the button
            const selectedOption = document.querySelector('.resolve-option.selected strong');
            autoResolveBtn.textContent = selectedOption ? 
                `Auto-Resolve (${selectedOption.textContent})` : 
                'Auto-Resolve Conflicts';
            autoResolveBtn.disabled = !this.selectedResolutionStrategy;
            
        } catch (error) {
            console.error('Error auto-resolving conflicts:', error);
            this.showError('Failed to auto-resolve conflicts');
            
            // Re-enable button
            const autoResolveBtn = document.getElementById('autoResolveBtn');
            const selectedOption = document.querySelector('.resolve-option.selected strong');
            autoResolveBtn.textContent = selectedOption ? 
                `Auto-Resolve (${selectedOption.textContent})` : 
                'Auto-Resolve Conflicts';
            autoResolveBtn.disabled = false;
        }
    }

    updateConflictCount() {
        const conflictCount = document.getElementById('conflictCount');
        if (this.conflictData) {
            const count = this.conflictData.totalConflicts;
            conflictCount.textContent = `${count} conflict${count !== 1 ? 's' : ''}`;
        }
    }

    showSuccess(message) {
        this.showNotification(message, 'success');
    }

    showError(message) {
        this.showNotification(message, 'error');
    }

    showNotification(message, type) {
        // Create notification element
        const notification = el('div', { className: `notification notification-${slug(type)}` },
            el('div', { className: 'notification-content' }, [
                icon(type === 'success' ? 'fa-check-circle' : 'fa-exclamation-circle'),
                el('span', { text: message })
            ]));

        // Add to page
        document.body.appendChild(notification);

        // Show with animation
        setTimeout(() => notification.classList.add('show'), 10);

        // Auto-remove after 5 seconds
        setTimeout(() => {
            notification.classList.remove('show');
            setTimeout(() => {
                if (notification.parentNode) {
                    notification.parentNode.removeChild(notification);
                }
            }, 300);
        }, 5000);
    }
}

// Utility functions
async function refreshConflicts() {
    if (window.conflictsManager) {
        await window.conflictsManager.loadConflicts();
        await window.conflictsManager.loadStats();
    }
}

async function refreshSources() {
    try {
        const response = await fetch('/api/patternselection/refresh-sources', {
            method: 'POST'
        });
        
        if (!response.ok) throw new Error('Failed to refresh sources');
        
        const result = await response.json();
        
        if (window.conflictsManager) {
            window.conflictsManager.showSuccess(`${result.message} - Found ${result.totalSources} sources, ${result.totalConflicts} conflicts`);
            await window.conflictsManager.loadConflicts();
            await window.conflictsManager.loadStats();
        }
        
    } catch (error) {
        console.error('Error refreshing sources:', error);
        if (window.conflictsManager) {
            window.conflictsManager.showError('Failed to refresh pattern sources');
        }
    }
}

// Initialize when DOM is loaded
document.addEventListener('DOMContentLoaded', () => {
    window.conflictsManager = new PatternConflictsManager();
    
    // Add CSS for notifications
    if (!document.getElementById('notification-styles')) {
        const style = document.createElement('style');
        style.id = 'notification-styles';
        style.textContent = `
            .notification {
                position: fixed;
                top: 20px;
                right: 20px;
                background: var(--card-bg);
                border: 1px solid var(--border-color);
                border-radius: 8px;
                padding: 15px;
                box-shadow: 0 4px 12px rgba(0, 0, 0, 0.3);
                z-index: 1000;
                transform: translateX(400px);
                opacity: 0;
                transition: all 0.3s ease;
                max-width: 400px;
            }
            
            .notification.show {
                transform: translateX(0);
                opacity: 1;
            }
            
            .notification-success {
                border-left: 4px solid var(--success-color, #28a745);
            }
            
            .notification-error {
                border-left: 4px solid var(--error-color, #dc3545);
            }
            
            .notification-content {
                display: flex;
                align-items: center;
                gap: 12px;
                color: var(--text-primary);
            }
            
            .notification-success .fas {
                color: var(--success-color, #28a745);
            }
            
            .notification-error .fas {
                color: var(--error-color, #dc3545);
            }
        `;
        document.head.appendChild(style);
    }
});