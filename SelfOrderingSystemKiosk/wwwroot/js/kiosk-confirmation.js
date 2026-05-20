// Highlight current step based on actual order status
    const confirmationConfig = window.kioskConfirmationConfig ?? {};
    const orderingSessionExpired = Boolean(confirmationConfig.orderingSessionExpired);
    const publicAccessToken = confirmationConfig.publicAccessToken || "";
    const confirmationQuickItems = confirmationConfig.confirmationQuickItems || [];
    const quickOrderCart = [];
    let confirmationSessionInfo = {
        hasSession: Boolean(confirmationConfig.hasOrderingSession),
        isExpired: Boolean(confirmationConfig.orderingSessionExpired),
        timeRemainingSeconds: Number(confirmationConfig.timeRemainingSeconds || 0)
    };
    const NEAR_EXPIRY_WARNING_SECONDS = 10 * 60;

    function confirmNearExpiryRefillIfNeeded() {
        if (!confirmationSessionInfo.hasSession || confirmationSessionInfo.isExpired) {
            return true;
        }

        const remaining = Number(confirmationSessionInfo.timeRemainingSeconds);
        if (!Number.isFinite(remaining) || remaining > NEAR_EXPIRY_WARNING_SECONDS) {
            return true;
        }

        return confirm('Your Unlimited session is close to ending. Refill requests near expiration are still for the active dine-in session and may not be suitable for takeout. Continue sending this refill?');
    }

    function renderQuickOrderCart() {
        const shell = document.getElementById('quickOrderCart');
        const list = document.getElementById('quickOrderCartList');
        if (!shell || !list) return;

        if (quickOrderCart.length === 0) {
            shell.style.display = 'none';
            list.innerHTML = '';
            return;
        }

        shell.style.display = '';
        list.innerHTML = quickOrderCart.map((item, index) => `
            <div class="quick-cart-row">
                <span><strong>${escapeQuickHtml(item.ItemName)}</strong> × ${item.Quantity}</span>
                <button type="button" data-remove-quick="${index}">Remove</button>
            </div>
        `).join('');
    }

    function escapeQuickHtml(value) {
        return String(value ?? '')
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    document.getElementById('confirmationQuickOrder')?.addEventListener('click', function(e) {
        const remove = e.target.closest('[data-remove-quick]');
        if (remove) {
            quickOrderCart.splice(Number(remove.getAttribute('data-remove-quick')), 1);
            renderQuickOrderCart();
            return;
        }

        const button = e.target.closest('.quick-order-item');
        if (!button) return;

        const name = button.getAttribute('data-name');
        const quantity = Number(button.getAttribute('data-quantity') || '1');
        const quickDefinition = confirmationQuickItems.find(item => item.Name === name);
        const existing = quickOrderCart.find(item => item.ItemName === name);
        if (existing) {
            const nextQuantity = existing.Quantity + (Number.isFinite(quantity) && quantity > 0 ? quantity : 1);
            if (quickDefinition?.IsWingFlavor && nextQuantity > 4) {
                alert('Maximum of 4 pieces per wing flavor.');
                return;
            }
            if (nextQuantity > 20) {
                alert('That quantity is too high for one quick order.');
                return;
            }
            existing.Quantity = nextQuantity;
        } else {
            quickOrderCart.push({
                ItemName: name,
                Quantity: Number.isFinite(quantity) && quantity > 0 ? quantity : 1,
                Price: 0
            });
        }
        renderQuickOrderCart();
    });

    document.getElementById('sendQuickOrder')?.addEventListener('click', function() {
        if (quickOrderCart.length === 0) return;
        if (!confirmNearExpiryRefillIfNeeded()) return;

        const button = this;
        const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
        button.disabled = true;
        button.innerHTML = '<i class="bi bi-hourglass-split"></i> Sending...';

        fetch(`/Customer/Kiosk/CreateUnlimitedRefill?orderNumber=${encodeURIComponent(confirmationConfig.orderNumber || "")}&accessToken=${encodeURIComponent(publicAccessToken)}`, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'RequestVerificationToken': token
            },
            body: JSON.stringify(quickOrderCart)
        })
        .then(response => response.json())
        .then(data => {
            if (data.success) {
                alert(data.message || 'Refill sent to the kitchen.');
                quickOrderCart.length = 0;
                renderQuickOrderCart();
                return;
            }

            alert(data.message || 'Unable to send refill.');
            if (data.sessionEnded) {
                disableReorderUnlimited();
                makeNewOrderStartNewSession();
            }
        })
        .catch(error => {
            console.error('Refill failed:', error);
            alert('Unable to send refill. Please try again.');
        })
        .finally(() => {
            button.disabled = false;
            button.innerHTML = '<i class="bi bi-send"></i> Send Refill';
        });
    });

    function disableReorderUnlimited() {
        document.querySelectorAll('.reorder-unlimited-action').forEach(reorderLink => {
            const disabled = document.createElement('button');
            disabled.type = 'button';
            disabled.className = reorderLink.classList.contains('quick-order-link') ? 'btn-secondary' : 'btn-secondary';
            disabled.disabled = true;
            disabled.style.opacity = '0.6';
            disabled.style.cursor = 'not-allowed';
            disabled.innerHTML = '<i class="bi bi-arrow-repeat"></i> Reorder Unavailable';
            reorderLink.replaceWith(disabled);
        });

        const quickPanel = document.getElementById('confirmationQuickOrder');
        if (quickPanel) quickPanel.style.display = 'none';
    }

    function makeNewOrderStartNewSession() {
        const newOrderLink = document.getElementById('newOrderLink');
        if (!newOrderLink) return;

        const url = new URL(newOrderLink.href, window.location.origin);
        url.searchParams.set('startNewSession', 'true');
        newOrderLink.href = url.toString();
    }

    if (orderingSessionExpired) {
        disableReorderUnlimited();
        makeNewOrderStartNewSession();
        setTimeout(() => {
            alert('Your ordering session is already over. You cannot add more orders to this session.');
        }, 350);
    }

    const orderStatus = (confirmationConfig.orderStatus || "").toLowerCase().replace(" ", "-");
    const statusSteps = ["pending", "in-progress", "completed"];
    const currentStepIndex = statusSteps.indexOf(orderStatus);
    
    document.querySelectorAll('.tracking-step-horizontal').forEach((step, index) => {
        const stepStatus = step.getAttribute('data-step');
        if (index < currentStepIndex) {
            step.classList.add('completed');
        } else if (index === currentStepIndex) {
            step.classList.add('in-progress');
        } else {
            step.classList.add('pending');
        }
    });

    // Update status badge
    const statusBadge = document.getElementById('statusBadge');
    if (statusBadge) {
        statusBadge.className = 'status-badge-large ' + orderStatus;
    }

    // Live order status updates, with slower HTTP fallback if SignalR is unavailable.
    let refreshInterval;
    let liveOrderConnection = null;
    let liveOrderConnected = false;
    let pendingOrderRefresh = false;
    let liveReconnectTimer = null;
    const indicator = document.getElementById('refreshIndicator');
    const statusNorm = (confirmationConfig.orderStatus || "").toLowerCase().replace(" ", "-");
    const pollMs = (statusNorm === 'completed' || statusNorm === 'canceled') ? 60000 : 30000;

    function showIndicator() {
        if (indicator) {
            indicator.classList.add('show');
        }
    }

    function hideIndicator() {
        if (indicator) {
            indicator.classList.remove('show');
        }
    }

    function stopOrderPolling() {
        if (refreshInterval) {
            clearInterval(refreshInterval);
            refreshInterval = null;
        }
    }

    function startOrderPolling() {
        if (liveOrderConnected || refreshInterval) {
            return;
        }

        refreshInterval = setInterval(checkOrderStatus, pollMs);
        setTimeout(checkOrderStatus, 2000);
    }

    function reloadForOrderChange() {
        if (document.hidden) {
            pendingOrderRefresh = true;
            return;
        }

        showIndicator();
        setTimeout(() => location.reload(), 300);
    }

    function checkOrderStatus() {
        if (document.hidden) {
            return; // Don't check if page is hidden
        }

        showIndicator();

        fetch(`/Customer/Kiosk/GetOrderStatus?orderNumber=${encodeURIComponent(confirmationConfig.orderNumber || "")}&accessToken=${encodeURIComponent(publicAccessToken)}`)
            .then(response => {
                if (!response.ok) {
                    throw new Error('Network response was not ok');
                }
                return response.json();
            })
            .then(data => {
                hideIndicator();

                if (data.status && data.status !== confirmationConfig.orderStatus) {
                    location.reload();
                }
            })
            .catch(error => {
                console.log('Status check failed:', error);
                hideIndicator();
            });
    }

    function scheduleOrderRealtimeReconnect() {
        if (liveReconnectTimer) return;
        liveReconnectTimer = setTimeout(() => {
            liveReconnectTimer = null;
            startOrderRealtime();
        }, 5000);
    }

    function startOrderRealtime() {
        if (!window.signalR || !confirmationConfig.orderNumber || !publicAccessToken) {
            startOrderPolling();
            return;
        }

        liveOrderConnection = new signalR.HubConnectionBuilder()
            .withUrl('/hubs/orders')
            .withAutomaticReconnect()
            .build();

        liveOrderConnection.on('OrderChanged', payload => {
            if (!payload || payload.orderNumber === confirmationConfig.orderNumber) {
                reloadForOrderChange();
            }
        });

        liveOrderConnection.onreconnected(() => {
            liveOrderConnected = true;
            stopOrderPolling();
            liveOrderConnection
                .invoke('WatchOrder', confirmationConfig.orderNumber || '', publicAccessToken || '')
                .catch(console.error);
        });

        liveOrderConnection.onclose(() => {
            liveOrderConnected = false;
            startOrderPolling();
            scheduleOrderRealtimeReconnect();
        });

        liveOrderConnection.start()
            .then(() => {
                liveOrderConnected = true;
                stopOrderPolling();
                return liveOrderConnection.invoke('WatchOrder', confirmationConfig.orderNumber || '', publicAccessToken || '');
            })
            .catch(error => {
                console.log('Live order updates unavailable. Falling back to status checks.', error);
                liveOrderConnected = false;
                startOrderPolling();
                scheduleOrderRealtimeReconnect();
            });
    }

    document.addEventListener('visibilitychange', function() {
        if (document.hidden) {
            stopOrderPolling();
        } else {
            if (pendingOrderRefresh) {
                pendingOrderRefresh = false;
                reloadForOrderChange();
                return;
            }

            if (!liveOrderConnected) startOrderPolling();
        }
    });

    startOrderRealtime();

    // Ordering session timer
    (function () {
        const timerBox = document.getElementById('confirmation-session-timer');
        const timerLabel = document.getElementById('confirmation-session-label');
        if (!timerBox || !timerLabel) return;

        function formatSessionTime(totalSeconds) {
            const safeSeconds = Math.max(0, Number(totalSeconds) || 0);
            const hours = Math.floor(safeSeconds / 3600);
            const minutes = Math.floor((safeSeconds % 3600) / 60);
            const seconds = safeSeconds % 60;
            return `${hours.toString().padStart(2, '0')}:${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}`;
        }

        function updateSessionTimer() {
            fetch(`/Customer/Kiosk/GetSessionInfo?orderNumber=${encodeURIComponent(confirmationConfig.orderNumber || "")}&accessToken=${encodeURIComponent(publicAccessToken)}`)
                .then(response => response.json())
                .then(data => {
                    confirmationSessionInfo = {
                        hasSession: Boolean(data.hasSession),
                        isExpired: Boolean(data.isExpired),
                        timeRemainingSeconds: Number(data.timeRemainingSeconds)
                    };

                    if (data.sessionEnded || data.isExpired) {
                        disableReorderUnlimited();
                        makeNewOrderStartNewSession();
                    }

                    if (!data.hasSession) {
                        timerBox.classList.remove('expired');
                        timerBox.style.display = 'none';
                        timerLabel.textContent = `${data.sessionHours || confirmationConfig.sessionHours || 2}-hour window starts when staff starts your first Unlimited order.`;
                        return;
                    }

                    timerBox.style.display = '';
                    if (data.isExpired) {
                        timerBox.classList.add('expired');
                        timerLabel.textContent = 'expired';
                        return;
                    }

                    timerBox.classList.remove('expired');
                    timerLabel.textContent = `${formatSessionTime(data.timeRemainingSeconds)} remaining`;
                })
                .catch(error => console.log('Session timer check failed:', error));
        }

        updateSessionTimer();
        setInterval(updateSessionTimer, 15000);
    })();

    // Cancel order function
    function cancelOrder(orderNumber) {
        if (!confirm('Are you sure you want to cancel this order?')) {
            return;
        }

        const cancelBtn = document.getElementById('cancelOrderBtn');
        const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
        if (cancelBtn) {
            cancelBtn.disabled = true;
            cancelBtn.innerHTML = '<i class="bi bi-hourglass-split"></i> Cancelling...';
        }

        fetch('/Customer/Kiosk/CancelOrder?orderNumber=' + encodeURIComponent(orderNumber) + '&accessToken=' + encodeURIComponent(publicAccessToken), {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
                'RequestVerificationToken': token
            }
        })
        .then(response => response.json())
        .then(data => {
            if (data.success) {
                alert('Order has been cancelled successfully.');
                location.reload();
            } else {
                alert('Error: ' + (data.message || 'Failed to cancel order.'));
                if (cancelBtn) {
                    cancelBtn.disabled = false;
                    cancelBtn.innerHTML = '<i class="bi bi-x-circle"></i> Cancel Order';
                }
            }
        })
        .catch(error => {
            console.error('Error cancelling order:', error);
            alert('An error occurred while cancelling the order. Please try again.');
            if (cancelBtn) {
                cancelBtn.disabled = false;
                cancelBtn.innerHTML = '<i class="bi bi-x-circle"></i> Cancel Order';
            }
        });
    }

