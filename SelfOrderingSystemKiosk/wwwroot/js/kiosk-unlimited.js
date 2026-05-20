let activeUnlimitedIncluded = null;
    let activeSulitMeal = null;
    let latestOrderingSessionInfo = {
        hasSession: false,
        isExpired: false,
        timeRemainingSeconds: null
    };
    const NEAR_EXPIRY_WARNING_SECONDS = 10 * 60;

    function confirmNearExpiryOrderIfNeeded() {
        if (!latestOrderingSessionInfo.hasSession || latestOrderingSessionInfo.isExpired) {
            return true;
        }

        const remaining = Number(latestOrderingSessionInfo.timeRemainingSeconds);
        if (!Number.isFinite(remaining) || remaining > NEAR_EXPIRY_WARNING_SECONDS) {
            return true;
        }

        return confirm('Your Unlimited session is close to ending. Orders placed near expiration are still for the active dine-in session and may not be suitable for takeout. Continue sending this order?');
    }

    function formatPeso(value) {
        return `₱${Number(value || 0).toFixed(2)}`;
    }

    function setSingleChoice(button, selector) {
        document.querySelectorAll(selector).forEach(choice => choice.classList.remove('selected'));
        button.classList.add('selected');
    }

    function selectedChoiceValue(selector, attrName) {
        return document.querySelector(`${selector}.selected`)?.getAttribute(attrName) || '';
    }

    function renderUnlimitedIncludedGroups(groups) {
        const container = document.getElementById('unlimitedIncludedGroups');
        if (!container) return;

        container.innerHTML = groups.map(group => {
            const limitText = group.maxSelections > 0
                ? `<small class="flavor-count" data-group-key="${escapeHtml(group.key)}">Choose up to ${group.maxSelections}</small>`
                : '';
            const choices = (group.options || []).map(option => `
                <button type="button"
                        class="sulit-choice unlimited-choice"
                        data-group-key="${escapeHtml(group.key)}"
                        data-group-title="${escapeHtml(group.title)}"
                        data-name="${escapeHtml(option.name)}"
                        data-label="${escapeHtml(option.label || option.name)}"
                        data-image="${escapeHtml(option.image || '')}"
                        data-quantity="${Number(group.defaultQuantity || 1)}">
                    ${option.image ? `<img src="${escapeHtml(option.image)}" alt="">` : ''}
                    <span>${escapeHtml(option.label || option.name)}</span>
                </button>
            `).join('');

            return `
                <div class="sulit-option-block" data-group-key="${escapeHtml(group.key)}" data-max-selections="${Number(group.maxSelections || 0)}">
                    <h4>${escapeHtml(group.title)} <span style="color:#777;font-size:0.82rem;font-weight:700;">${escapeHtml(group.hint || '')}</span></h4>
                    <div class="sulit-choice-grid">${choices}</div>
                    ${limitText}
                </div>
            `;
        }).join('');
    }

    function selectedUnlimitedChoices() {
        return Array.from(document.querySelectorAll('#unlimitedIncludedGroups .unlimited-choice.selected'));
    }

    function updateUnlimitedChoiceState(groupBlock) {
        if (!groupBlock) return;

        const maxSelections = Number(groupBlock.getAttribute('data-max-selections') || '0');
        const selected = Array.from(groupBlock.querySelectorAll('.unlimited-choice.selected'));
        const atLimit = maxSelections > 0 && selected.length >= maxSelections;
        groupBlock.querySelectorAll('.unlimited-choice').forEach(button => {
            button.disabled = atLimit && !button.classList.contains('selected');
        });

        const count = groupBlock.querySelector('.flavor-count');
        if (count && maxSelections > 0) {
            count.textContent = selected.length === 0
                ? `Choose up to ${maxSelections}`
                : `${selected.length}/${maxSelections} selected`;
        }
    }

    function openUnlimitedIncludedScreen(button) {
        if (personCount === 0) {
            showNotification("Please enter the number of persons first.", 'error');
            openModal("personModal");
            return;
        }

        const groups = JSON.parse(button.getAttribute('data-groups') || '[]');
        if (groups.length === 0) return;

        activeUnlimitedIncluded = {
            image: button.getAttribute('data-image') || '',
            groups
        };

        const image = document.getElementById('unlimitedIncludedImage');
        if (image) image.src = activeUnlimitedIncluded.image;

        renderUnlimitedIncludedGroups(groups);
        document.querySelectorAll('#unlimitedIncludedGroups .sulit-option-block').forEach(updateUnlimitedChoiceState);

        const screen = document.getElementById('unlimitedIncludedScreen');
        screen?.classList.add('active');
        screen?.setAttribute('aria-hidden', 'false');
    }

    function closeUnlimitedIncludedScreen() {
        const screen = document.getElementById('unlimitedIncludedScreen');
        screen?.classList.remove('active');
        screen?.setAttribute('aria-hidden', 'true');
        activeUnlimitedIncluded = null;
    }

    function updateSulitMealPrice() {
        if (!activeSulitMeal) return;
        const selectedService = document.querySelector('#sulitServiceGrid .sulit-choice.selected');
        const price = Number(selectedService?.getAttribute('data-price') || '0');
        const priceEl = document.getElementById('sulitMealPrice');
        if (priceEl) priceEl.textContent = formatPeso(price);
    }

    function openSulitMealScreen(button) {
        if (personCount === 0) {
            showNotification("Please enter the number of persons first.", 'error');
            openModal("personModal");
            return;
        }

        const variants = JSON.parse(button.getAttribute('data-variants') || '[]');
        if (variants.length === 0) return;

        activeSulitMeal = {
            baseName: button.getAttribute('data-base-name') || 'Sulit Kap Meal',
            image: button.getAttribute('data-image') || '',
            variants
        };

        const screen = document.getElementById('sulitMealScreen');
        const image = document.getElementById('sulitMealImage');
        const title = document.getElementById('sulitMealTitle');
        const serviceGrid = document.getElementById('sulitServiceGrid');

        if (image) image.src = activeSulitMeal.image;
        if (title) title.textContent = activeSulitMeal.baseName;
        if (serviceGrid) {
            serviceGrid.innerHTML = variants.map((variant, index) => `
                <button type="button"
                        class="sulit-choice service-choice ${index === 0 ? 'selected' : ''}"
                        data-name="${escapeHtml(variant.name)}"
                        data-label="${escapeHtml(variant.label)}"
                        data-price="${variant.price}"
                        data-image="${escapeHtml(variant.image || activeSulitMeal.image)}">
                    ${escapeHtml(variant.label)} - ${formatPeso(variant.price)}
                </button>
            `).join('');
        }

        document.querySelectorAll('#sulitFlavorGrid .sulit-choice').forEach(choice => choice.classList.remove('selected'));
        document.querySelector('#sulitFlavorGrid .sulit-choice')?.classList.add('selected');
        document.querySelectorAll('.rice-choice').forEach(choice => choice.classList.remove('selected'));
        document.querySelector('.rice-choice')?.classList.add('selected');
        updateSulitMealPrice();

        screen?.classList.add('active');
        screen?.setAttribute('aria-hidden', 'false');
    }

    function closeSulitMealScreen() {
        const screen = document.getElementById('sulitMealScreen');
        screen?.classList.remove('active');
        screen?.setAttribute('aria-hidden', 'true');
        activeSulitMeal = null;
    }

    document.querySelectorAll('.customize-unlimited-included').forEach(button => {
        button.addEventListener('click', function() {
            openUnlimitedIncludedScreen(this);
        });
    });

    document.querySelector('.unlimited-included-back-btn')?.addEventListener('click', closeUnlimitedIncludedScreen);

    document.getElementById('unlimitedIncludedGroups')?.addEventListener('click', function(e) {
        const button = e.target.closest('.unlimited-choice');
        if (!button || button.disabled) return;

        button.classList.toggle('selected');
        updateUnlimitedChoiceState(button.closest('.sulit-option-block'));
    });

    document.getElementById('addUnlimitedIncludedToCart')?.addEventListener('click', function() {
        if (!activeUnlimitedIncluded) return;

        const selected = selectedUnlimitedChoices();
        if (selected.length === 0) {
            showNotification('Please choose at least one unlimited item.', 'error');
            return;
        }

        const quantityLimit = getQuantityLimit();
        const flavorLimit = getFlavorLimit();
        const selectedWingChoices = selected.filter(choice => choice.getAttribute('data-group-key') === 'wings');
        const existingWingNames = getSelectedWingFlavorNames ? getSelectedWingFlavorNames() : new Set();
        const newWingNames = selectedWingChoices
            .map(choice => String(choice.getAttribute('data-name') || '').toLowerCase())
            .filter(name => name && !existingWingNames.has(name));

        if (existingWingNames.size + new Set(newWingNames).size > flavorLimit) {
            showNotification(`This table can only have up to ${flavorLimit} wing flavors. Please choose from the current flavors.`, 'error');
            return;
        }

        for (const choice of selected) {
            const name = choice.getAttribute('data-name');
            const groupKey = choice.getAttribute('data-group-key');
            const quantity = Number(choice.getAttribute('data-quantity') || '1');
            const isWingFlavor = groupKey === 'wings';
            const newItem = {
                name,
                image: choice.getAttribute('data-image') || activeUnlimitedIncluded.image,
                price: 0,
                quantity: Number.isFinite(quantity) && quantity > 0 ? quantity : 1,
                isWingFlavor,
                isAlaCarteAddOn: false,
                variantLabel: choice.getAttribute('data-group-title') || ''
            };

            const existingItem = cart.find(item => cartKey(item) === cartKey(newItem));
            if (existingItem) {
                if (isWingFlavor && existingItem.quantity + newItem.quantity > quantityLimit) {
                    showNotification(`You can only add up to ${quantityLimit} pieces per wing flavor.`, 'error');
                    return;
                }

                existingItem.quantity += newItem.quantity;
            } else {
                cart.push(newItem);
            }
        }

        updateCartDisplay();
        closeUnlimitedIncludedScreen();
        showNotification('Added unlimited selections to cart.', 'success');
    });

    document.querySelectorAll('.customize-sulit-meal').forEach(button => {
        button.addEventListener('click', function() {
            openSulitMealScreen(this);
        });
    });

    document.querySelector('.sulit-meal-back-btn')?.addEventListener('click', closeSulitMealScreen);

    document.getElementById('sulitFlavorGrid')?.addEventListener('click', function(e) {
        const button = e.target.closest('.sulit-choice');
        if (!button) return;
        setSingleChoice(button, '#sulitFlavorGrid .sulit-choice');
    });

    document.querySelectorAll('.rice-choice').forEach(button => {
        button.addEventListener('click', function() {
            setSingleChoice(this, '.rice-choice');
        });
    });

    document.getElementById('sulitServiceGrid')?.addEventListener('click', function(e) {
        const button = e.target.closest('.service-choice');
        if (!button) return;
        setSingleChoice(button, '#sulitServiceGrid .service-choice');
        updateSulitMealPrice();
    });

    document.getElementById('addSulitMealToCart')?.addEventListener('click', function() {
        if (!activeSulitMeal) return;

        const service = document.querySelector('#sulitServiceGrid .service-choice.selected');
        const flavor = selectedChoiceValue('#sulitFlavorGrid .sulit-choice', 'data-flavor');
        const rice = selectedChoiceValue('.rice-choice', 'data-rice');

        if (!service || !flavor || !rice) {
            alert('Please complete the meal selections.');
            return;
        }

        const price = Number(service.getAttribute('data-price') || '0');
        const newItem = {
            name: service.getAttribute('data-name'),
            price: Number.isFinite(price) ? price : 0,
            image: service.getAttribute('data-image') || activeSulitMeal.image,
            quantity: 1,
            isWingFlavor: false,
            isAlaCarteAddOn: Number.isFinite(price) && price > 0,
            flavors: [flavor, rice],
            variants: [],
            variantLabel: service.getAttribute('data-label') || ''
        };

        const existingItem = cart.find(item => cartKey(item) === cartKey(newItem));
        if (existingItem) {
            const quantityLimit = getQuantityLimit();
            if (existingItem.quantity >= quantityLimit) {
                showNotification(`You can only add up to ${quantityLimit} per Ala Carte add-on.`, 'error');
                return;
            }
            existingItem.quantity += 1;
        } else {
            cart.push(newItem);
        }

        updateCartDisplay();
        closeSulitMealScreen();
        showNotification('Added customized meal to cart.', 'success');
    });

    // Category filter functionality
    document.querySelectorAll('.menu-tabs button').forEach(button => {
        button.addEventListener('click', function() {
            document.querySelectorAll('.menu-tabs button').forEach(btn => btn.classList.remove('active'));
            this.classList.add('active');

            const filter = this.getAttribute('data-filter');
            document.querySelectorAll('.menu-row').forEach(row => {
                row.style.display = row.getAttribute('data-category') === filter ? '' : 'none';
            });
        });
    });

    const defaultUnlimitedFilter = document.querySelector('.menu-tabs button.active');
    if (defaultUnlimitedFilter) {
        defaultUnlimitedFilter.click();
    }

    // Confirm Order
    document.querySelector('.confirm-btn').addEventListener('click', function() {
        if(cart.length === 0){
            alert("Your cart is empty!");
            return;
        }

        // For reorders, personCount should already be set from previous order
        // For new orders, require personCount input
        if(personCount === 0 && !window.isReorder){
            alert("Please enter the number of persons first!");
            openModal("personModal");
            return;
        }
        
        // If it's a reorder but personCount is still 0, something went wrong
        if(personCount === 0 && window.isReorder){
            alert("Error: Unable to determine number of persons. Please start a new order.");
            return;
        }

        if (!confirmNearExpiryOrderIfNeeded()) {
            return;
        }

        // Prepare order items
        let orderItems = cart.map(c => ({
            ItemName: c.flavors && c.flavors.length > 0
                ? `${c.name} (Flavors: ${c.flavors.join(', ')})`
                : c.name,
            Quantity: c.quantity,
            Price: c.price || 0
        }));

        // Send personCount as query parameter for unlimited orders
        const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value || '';
        fetch(`/Customer/Kiosk/ConfirmOrder?orderType=Unlimited&personCount=${personCount}`, {
        method: 'POST',
        headers: {
            'Content-Type': 'application/json',
            'RequestVerificationToken': token
        },
        body: JSON.stringify(orderItems)
    })
        .then(async res => {
            if (!res.ok) {
                const errorText = await res.text();
                console.error('Server error:', errorText);
                alert('Error creating order. Please try again.');
                return;
            }
            return res.json();
        })
        .then(data => {
            if(data && data.success){
                clearDeviceOrderState();
                // Redirect to order tracking page
                window.location.href = '/Customer/Kiosk/Confirmation?orderNumber=' + encodeURIComponent(data.orderNumber) + '&accessToken=' + encodeURIComponent(data.accessToken || '');
            } else {
                if (data?.resetPersonCount) {
                    personCount = 0;
                    cart = [];
                    clearDeviceOrderState();
                    window.isReorder = false;
                    window.reorderPersonCount = 0;
                    window.orderingSessionPersonCount = 0;
                    const personCountLabel = document.querySelector(".person-count");
                    if (personCountLabel) personCountLabel.textContent = "0 Persons";
                    updateCartDisplay();
                    setPersonModalMode("initial");
                    openModal("personModal");
                }
                alert(data?.message || 'Error creating order. Please try again.');
            }
        })
        .catch(err => {
            console.error('Error:', err);
            alert('Error creating order. Please try again.');
        });
    });

    // Session timer
    (function() {
        let timerInterval = null;
        let endedSessionHandled = false;

        function formatSessionTime(totalSeconds) {
            const safeSeconds = Math.max(0, Number(totalSeconds) || 0);
            const hours = Math.floor(safeSeconds / 3600);
            const minutes = Math.floor((safeSeconds % 3600) / 60);
            const seconds = safeSeconds % 60;
            return `${hours.toString().padStart(2, '0')}:${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}`;
        }

        function updateTimer() {
            fetch('/Customer/Kiosk/GetSessionInfo')
                .then(response => response.json())
                .then(data => {
                    latestOrderingSessionInfo = {
                        hasSession: Boolean(data.hasSession),
                        isExpired: Boolean(data.isExpired),
                        timeRemainingSeconds: Number(data.timeRemainingSeconds)
                    };

                    const timerDisplay = document.getElementById('timer-display');
                    const timerContainer = document.getElementById('session-timer');
                    const expiredContainer = document.getElementById('session-timer-expired');

                    if ((data.sessionEnded || data.resetPersonCount) && !endedSessionHandled) {
                        endedSessionHandled = true;
                        personCount = 0;
                        cart = [];
                        clearDeviceOrderState();
                        window.isReorder = false;
                        window.reorderPersonCount = 0;
                        window.orderingSessionPersonCount = 0;
                        const personCountLabel = document.querySelector(".person-count");
                        if (personCountLabel) personCountLabel.textContent = "0 Persons";
                        updateCartDisplay();
                        setPersonModalMode("initial");
                        openModal("personModal");
                    }

                    if (data.hasSession) {
                        const submitBtn = document.querySelector('button[type="submit"]') || 
                                        document.querySelector('.confirm-btn') ||
                                        document.querySelector('.btn-primary');
                        
                        if (data.isExpired) {
                            timerContainer.style.display = 'none';
                            expiredContainer.style.display = 'block';
                            if (submitBtn) {
                                submitBtn.disabled = true;
                                submitBtn.style.opacity = '0.5';
                                submitBtn.style.cursor = 'not-allowed';
                                submitBtn.title = 'Session expired. Please start a new order.';
                            }
                            if (timerInterval) {
                                clearInterval(timerInterval);
                            }
                        } else {
                            timerContainer.style.display = 'block';
                            expiredContainer.style.display = 'none';
                            
                            timerDisplay.textContent = formatSessionTime(data.timeRemainingSeconds);
                            
                            // Change color when less than 10 minutes remaining
                            if (data.timeRemainingSeconds < 600) {
                                timerContainer.style.background = '#fff3e0';
                                timerContainer.style.borderColor = '#ff9800';
                                timerDisplay.style.color = '#e65100';
                            }
                            
                            // Enable order button if it was disabled
                            if (submitBtn) {
                                submitBtn.disabled = false;
                                submitBtn.style.opacity = '1';
                                submitBtn.style.cursor = 'pointer';
                                submitBtn.title = '';
                            }
                        }
                    } else {
                        timerContainer.style.display = 'none';
                        expiredContainer.style.display = 'none';
                    }
                })
                .catch(error => {
                    console.error('Error fetching session info:', error);
                });
        }

        updateTimer();
        timerInterval = setInterval(updateTimer, 15000);
    })();

