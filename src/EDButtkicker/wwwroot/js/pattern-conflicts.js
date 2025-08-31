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

        const statsCard = document.getElementById('statsCard');
        statsCard.innerHTML = `
            <div class="stat-item">
                <span class="stat-label">Total Ship/Event Combinations</span>
                <span class="stat-value">${this.stats.totalShipEventCombinations}</span>
            </div>
            <div class="stat-item">
                <span class="stat-label">Available Patterns</span>
                <span class="stat-value">${this.stats.totalAvailablePatterns}</span>
            </div>
            <div class="stat-item">
                <span class="stat-label">Conflicting Combinations</span>
                <span class="stat-value">${this.stats.conflictingCombinations}</span>
            </div>
            <div class="stat-item">
                <span class="stat-label">File System Patterns</span>
                <span class="stat-value">${this.stats.fileSystemPatterns}</span>
            </div>
            <div class="stat-item">
                <span class="stat-label">User Custom Patterns</span>
                <span class="stat-value">${this.stats.userCustomPatterns}</span>
            </div>
            <div class="stat-item">
                <span class="stat-label">Default Patterns</span>
                <span class="stat-value">${this.stats.defaultPatterns}</span>
            </div>
        `;
    }

    renderConflicts() {
        const conflictsList = document.getElementById('conflictsList');
        
        if (!this.conflictData || this.conflictData.conflicts.length === 0) {
            conflictsList.innerHTML = `
                <div class="no-conflicts">
                    <i class="fas fa-check-circle"></i>
                    <h3>No Pattern Conflicts</h3>
                    <p>All ship/event combinations have been resolved or only have one pattern available.</p>
                </div>
            `;
            return;
        }

        const conflictsHtml = this.conflictData.conflicts.map(conflict => 
            this.renderConflictCard(conflict)
        ).join('');
        
        conflictsList.innerHTML = conflictsHtml;
        this.attachConflictHandlers();
    }

    renderConflictCard(conflict) {
        const activePatternId = conflict.activePattern?.sourceId || '';
        
        return `
            <div class="conflict-card has-conflicts" data-ship-type="${conflict.shipType}" data-event="${conflict.eventName}">
                <div class="conflict-header">
                    <div class="conflict-title">${conflict.shipType} - ${conflict.eventName}</div>
                    <div class="conflict-subtitle">
                        <span class="conflict-badge">${conflict.conflictCount} patterns available</span>
                    </div>
                </div>
                <div class="conflict-body">
                    <div class="pattern-options">
                        ${conflict.availablePatterns.map(pattern => `
                            <div class="pattern-option ${pattern.sourceId === activePatternId ? 'active' : ''}" 
                                 data-source-id="${pattern.sourceId}">
                                <input type="radio" name="pattern_${conflict.shipType}_${conflict.eventName}" 
                                       value="${pattern.sourceId}" ${pattern.sourceId === activePatternId ? 'checked' : ''}>
                                <div class="pattern-info">
                                    <div class="pattern-name">${pattern.sourceName}</div>
                                    <div class="pattern-details">
                                        <span>Type: ${pattern.patternType}</span>
                                        <span>Freq: ${pattern.frequency}Hz</span>
                                        <span>Int: ${pattern.intensity}%</span>
                                        <span>Dur: ${pattern.duration}ms</span>
                                    </div>
                                    <div class="pattern-meta">
                                        <span class="source-type-badge source-type-${pattern.sourceType.toLowerCase()}">${pattern.sourceType}</span>
                                        ${pattern.packName ? `<span>Pack: ${pattern.packName}</span>` : ''}
                                        ${pattern.author ? `<span>Author: ${pattern.author}</span>` : ''}
                                        ${pattern.version ? `<span>v${pattern.version}</span>` : ''}
                                    </div>
                                </div>
                            </div>
                        `).join('')}
                    </div>
                </div>
            </div>
        `;
    }

    attachConflictHandlers() {
        // Attach click handlers to pattern options
        document.querySelectorAll('.pattern-option').forEach(option => {
            option.addEventListener('click', (e) => {
                if (e.target.type === 'radio') return; // Let radio handle itself
                
                const radio = option.querySelector('input[type="radio"]');
                if (radio) {
                    radio.checked = true;
                    this.selectPattern(option);
                }
            });
        });

        // Attach change handlers to radio buttons
        document.querySelectorAll('input[type="radio"]').forEach(radio => {
            radio.addEventListener('change', (e) => {
                if (e.target.checked) {
                    const option = e.target.closest('.pattern-option');
                    this.selectPattern(option);
                }
            });
        });
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
            autoResolveBtn.innerHTML = '<i class="fas fa-spinner fa-spin"></i> Resolving...';

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
        const notification = document.createElement('div');
        notification.className = `notification notification-${type}`;
        notification.innerHTML = `
            <div class="notification-content">
                <i class="fas ${type === 'success' ? 'fa-check-circle' : 'fa-exclamation-circle'}"></i>
                <span>${message}</span>
            </div>
        `;

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