# 정밀정렬 스캔 코디네이터
# 오렌지보드(USB COM) + 나노(HC-06 블루투스 COM) 두 포트를 잡고
# step-and-measure 방식으로 스캔 → 피크 스텝 복귀
#
# pip install pyserial matplotlib

import time
import csv
import serial
import matplotlib.pyplot as plt

# ===== 설정 =====
PORT_MOTOR = "COM11"     # 오렌지보드 USB 포트
PORT_SENSOR = "COM4"     # HC-06 페어링 후 생성된 '발신(outgoing)' COM 포트
BAUD = 9600

SCAN_STEPS = 3200        # 총 스캔 거리 (스텝) — 1/8 마이크로스테핑 1600스텝/rev 기준 2회전
STEP_INC = 50            # 측정 간격 (스텝) — 작을수록 정밀, 오래 걸림
SETTLE_S = 0.15          # 이동 후 전력 안정화 대기 (초)
# ================


def send_cmd(ser, cmd, expect, timeout=15):
    """명령 전송 후 expect로 시작하는 응답 줄을 기다림"""
    ser.reset_input_buffer()
    ser.write((cmd + "\n").encode())
    t0 = time.time()
    while time.time() - t0 < timeout:
        line = ser.readline().decode(errors="ignore").strip()
        if line.startswith(expect):
            return line
    raise TimeoutError(f"'{cmd}' 응답 없음 (expect '{expect}')")


def read_vip(ser):
    """(전압 V, 전류 mA, 전력 mW) 튜플 반환"""
    line = send_cmd(ser, "R", "P")          # 예: "P 5.02,1450.5,7285.6"
    v, i, p = line.split()[1].split(",")
    return float(v), float(i), float(p)


def main():
    motor = serial.Serial(PORT_MOTOR, BAUD, timeout=1)
    time.sleep(2.5)  # 아두이노는 포트 열릴 때 자동 리셋됨 → 부팅 대기 필수
    sensor = serial.Serial(PORT_SENSOR, BAUD, timeout=1)
    time.sleep(1.0)
    sensor.write(b"s\n")         # 스트리밍이 켜져 있었을 경우 대비해 중지
    time.sleep(0.3)
    sensor.reset_input_buffer()

    try:
        run_scan(motor, sensor)
    except KeyboardInterrupt:
        # 비상정지: Ctrl+C → 진행 중인 이동 즉시 중단
        motor.write(b"X\n")
        line = motor.readline().decode(errors="ignore").strip()
        print(f"\n*** 비상정지 *** ({line})")
    finally:
        motor.close()
        sensor.close()


def run_scan(motor, sensor):
    input(f"\n{SCAN_STEPS}스텝 스캔을 시작합니다. 준비되면 Enter (취소: Ctrl+C) > ")

    send_cmd(motor, "Z", "OK")   # 현재 위치를 원점(0)으로
    print("스캔 시작")

    records = []  # (step, V, mA, mW)
    pos = 0
    while pos <= SCAN_STEPS:
        v, i, p = read_vip(sensor)
        records.append((pos, v, i, p))
        print(f"  step {pos:5d}  →  {v:5.2f} V  {i:8.1f} mA  {p:8.1f} mW")
        resp = send_cmd(motor, f"M{STEP_INC}", "OK")
        pos = int(resp.split()[1])
        time.sleep(SETTLE_S)

    peak_step, _, _, peak_p = max(records, key=lambda r: r[3])
    print(f"\n피크: step {peak_step} ({peak_p:.1f} mW) → 복귀 중...")
    send_cmd(motor, f"G{peak_step}", "OK")

    # 복귀 후 검증 측정
    time.sleep(SETTLE_S)
    _, _, p_now = read_vip(sensor)
    print(f"복귀 완료. 현재 전력: {p_now:.1f} mW")

    # 스캔 곡선 CSV 저장 (그래프/보고서용)
    with open("scan_log.csv", "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["step", "voltage_V", "current_mA", "power_mW"])
        w.writerows(records)
    print("scan_log.csv 저장 완료")

    plot_scan(records, peak_step, peak_p)


def plot_scan(records, peak_step, peak_p):
    steps = [r[0] for r in records]
    volts = [r[1] for r in records]
    currs = [r[2] for r in records]
    pwrs  = [r[3] for r in records]

    fig, (ax1, ax2, ax3) = plt.subplots(3, 1, sharex=True, figsize=(9, 8))

    ax1.plot(steps, volts, "-o", ms=3, color="tab:blue")
    ax1.set_ylabel("Voltage [V]")
    ax2.plot(steps, currs, "-o", ms=3, color="tab:orange")
    ax2.set_ylabel("Current [mA]")
    ax3.plot(steps, pwrs, "-o", ms=3, color="tab:red")
    ax3.set_ylabel("Power [mW]")
    ax3.set_xlabel("Step")

    # 피크 표시: 세 그래프 모두 세로선 + 전력 그래프에 마커/주석
    for ax in (ax1, ax2, ax3):
        ax.axvline(peak_step, color="green", ls="--", lw=1)
        ax.grid(alpha=0.3)
    ax3.plot(peak_step, peak_p, "g*", ms=15)
    ax3.annotate(f"peak: step {peak_step}\n{peak_p:.1f} mW",
                 xy=(peak_step, peak_p), xytext=(10, -15),
                 textcoords="offset points", color="green")

    fig.suptitle("Wireless Charging Alignment Scan")
    fig.tight_layout()
    fig.savefig("scan_plot.png", dpi=150)
    print("scan_plot.png 저장 완료")
    plt.show()


if __name__ == "__main__":
    main()
