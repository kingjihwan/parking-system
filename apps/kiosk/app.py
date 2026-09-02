"""키오스크 차량 충전 정보 입력 서버.

차주가 키오스크 화면에서 주차 구역, 차량 번호, 배터리 잔량, 예상 출차 시간을
입력하면 SQLite에 저장하고, Unity 쪽에서는 /api/vehicles 를 주기적으로 폴링해
최신 데이터를 가져갈 수 있다.
"""

import sqlite3
from datetime import datetime
from pathlib import Path

from flask import Flask, g, jsonify, render_template, request

DB_PATH = Path(__file__).parent / "kiosk.db"

app = Flask(__name__)
app.json.ensure_ascii = False


def get_db():
    if "db" not in g:
        g.db = sqlite3.connect(DB_PATH)
        g.db.row_factory = sqlite3.Row
    return g.db


@app.teardown_appcontext
def close_db(exception=None):
    db = g.pop("db", None)
    if db is not None:
        db.close()


def init_db():
    db = sqlite3.connect(DB_PATH)
    db.execute(
        """
        CREATE TABLE IF NOT EXISTS vehicles (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            parking_spot TEXT NOT NULL,
            license_plate TEXT NOT NULL,
            vehicle_model TEXT,
            battery_level INTEGER NOT NULL,
            expected_departure_time TEXT NOT NULL,
            created_at TEXT NOT NULL
        )
        """
    )
    db.commit()
    db.close()


@app.route("/")
def kiosk():
    return render_template("kiosk.html")


@app.route("/status")
def status():
    return render_template("status.html")


@app.route("/api/vehicles", methods=["GET"])
def list_vehicles():
    db = get_db()
    rows = db.execute("SELECT * FROM vehicles ORDER BY created_at DESC").fetchall()
    return jsonify([dict(row) for row in rows])


@app.route("/api/vehicles", methods=["POST"])
def create_vehicle():
    data = request.get_json(silent=True) or request.form

    parking_spot = (data.get("parking_spot") or "").strip()
    license_plate = (data.get("license_plate") or "").strip()
    vehicle_model = (data.get("vehicle_model") or "").strip()
    battery_level = data.get("battery_level")
    expected_departure_time = (data.get("expected_departure_time") or "").strip()

    if not parking_spot or not license_plate or not expected_departure_time:
        return jsonify({"error": "주차 구역, 차량 번호, 예상 출차 시간은 필수입니다."}), 400

    try:
        battery_level = int(battery_level)
    except (TypeError, ValueError):
        return jsonify({"error": "배터리 잔량은 숫자(%)로 입력해야 합니다."}), 400

    if not 0 <= battery_level <= 100:
        return jsonify({"error": "배터리 잔량은 0~100 사이여야 합니다."}), 400

    db = get_db()
    db.execute(
        """
        INSERT INTO vehicles
            (parking_spot, license_plate, vehicle_model, battery_level, expected_departure_time, created_at)
        VALUES (?, ?, ?, ?, ?, ?)
        """,
        (
            parking_spot,
            license_plate,
            vehicle_model,
            battery_level,
            expected_departure_time,
            datetime.now().isoformat(timespec="seconds"),
        ),
    )
    db.commit()
    return jsonify({"message": "등록 완료"}), 201


@app.route("/api/vehicles/<int:vehicle_id>", methods=["DELETE"])
def delete_vehicle(vehicle_id):
    db = get_db()
    db.execute("DELETE FROM vehicles WHERE id = ?", (vehicle_id,))
    db.commit()
    return jsonify({"message": "삭제 완료"})


init_db()

if __name__ == "__main__":
    app.run(host="0.0.0.0", port=5000, debug=True)