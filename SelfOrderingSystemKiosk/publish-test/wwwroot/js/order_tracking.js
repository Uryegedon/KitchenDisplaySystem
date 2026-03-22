document.addEventListener('DOMContentLoaded', () => {
    const steps = document.querySelectorAll('.tracking-step');
    let currentStep = 0;

    function activateStep(step) {
        steps.forEach((s, i) => s.classList.toggle('active', i <= step));
    }

    // Animate steps every 3 seconds
    const interval = setInterval(() => {
        if (currentStep < steps.length) {
            activateStep(currentStep);
            currentStep++;
        } else {
            clearInterval(interval);
        }
    }, 3000);
});
