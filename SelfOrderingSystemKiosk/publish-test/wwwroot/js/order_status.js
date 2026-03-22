// Simulate order status updates
const statusSteps = document.querySelectorAll('.status-item');
let currentStep = 0;

// Function to update status dynamically
function updateStatus() {
    if (currentStep > 0) {
        statusSteps[currentStep - 1].classList.remove('active');
        statusSteps[currentStep - 1].classList.add('completed');
    }

    if (currentStep < statusSteps.length) {
        statusSteps[currentStep].classList.add('active');
        currentStep++;
    } else {
        // Order is ready, show modal
        openModal('orderCompleteModal');
        clearInterval(statusInterval);
    }
}

// Open modal function
function openModal(id) {
    const modal = document.getElementById(id);
    if (modal) modal.classList.add('active');
}

// Close modal function
function closeModal(id) {
    const modal = document.getElementById(id);
    if (modal) modal.classList.remove('active');
}

// Event listeners for modal buttons
document.addEventListener('DOMContentLoaded', () => {
    const orderAgainBtn = document.getElementById('orderAgainBtn');
    const endSessionBtn = document.getElementById('endSessionBtn');

    if (orderAgainBtn) {
        orderAgainBtn.addEventListener('click', () => {
            window.location.href = '/Customer/Menu/UnlimitedMenu'; // redirect to menu
        });
    }

    if (endSessionBtn) {
        endSessionBtn.addEventListener('click', () => {
            window.location.href = '/Customer/Home/EndSession'; // redirect to billout
        });
    }
});

// Start automatic status updates for testing (every 3 seconds)
const statusInterval = setInterval(updateStatus, 3000);
