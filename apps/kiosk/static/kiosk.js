const form = document.getElementById("vehicle-form");
const errorEl = document.getElementById("form-error");
const overlay = document.getElementById("confirm-overlay");
const slider = document.getElementById("battery-slider");
const sliderOutput = document.getElementById("battery-output");

slider.addEventListener("input", () => {
  sliderOutput.textContent = `${slider.value}%`;
});

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  errorEl.hidden = true;

  const formData = new FormData(form);
  const payload = Object.fromEntries(formData.entries());

  try {
    const response = await fetch("/api/vehicles", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    });

    const result = await response.json();

    if (!response.ok) {
      throw new Error(result.error || "등록 중 오류가 발생했습니다.");
    }

    showConfirmation();
  } catch (err) {
    errorEl.textContent = err.message;
    errorEl.hidden = false;
  }
});

function showConfirmation() {
  overlay.hidden = false;
  setTimeout(() => {
    overlay.hidden = true;
    form.reset();
    slider.value = 50;
    sliderOutput.textContent = "50%";
  }, 3000);
}