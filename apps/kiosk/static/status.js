const tbody = document.querySelector("#vehicle-table tbody");

async function refresh() {
  const response = await fetch("/api/vehicles");
  const vehicles = await response.json();

  tbody.innerHTML = "";
  for (const v of vehicles) {
    // T를 <br> 태그로 교체하여 날짜와 시간을 두 줄로 나눕니다.
    const formattedDate = v.created_at.replace("T", "<br>");

    const row = document.createElement("tr");
    row.innerHTML = `
      <td>${v.parking_spot}</td>
      <td>${v.license_plate}</td>
      <td>${v.vehicle_model || "-"}</td>
      <td>${v.battery_level}%</td>
      <td>${v.expected_departure_time}</td>
      <td>${formattedDate}</td>
      <td><button class="delete-btn" data-id="${v.id}">삭제</button></td>
    `;
    tbody.appendChild(row);
  }

  for (const btn of tbody.querySelectorAll(".delete-btn")) {
    btn.addEventListener("click", async () => {
      await fetch(`/api/vehicles/${btn.dataset.id}`, { method: "DELETE" });
      refresh();
    });
  }
}

refresh();
setInterval(refresh, 10000);
