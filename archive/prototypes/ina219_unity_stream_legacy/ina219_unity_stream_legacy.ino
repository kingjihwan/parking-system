#include <Wire.h>
#include <Adafruit_INA219.h>
#include <SoftwareSerial.h>

Adafruit_INA219 ina219;

SoftwareSerial BT(2, 3);

bool measurementEnabled = false;
unsigned long previousMillis = 0;
const unsigned long measurementInterval = 1000;

void setup() {
  Serial.begin(9600);
  BT.begin(9600);

  if (!ina219.begin()) {
    Serial.println("INA219 연결 실패");
    BT.println("INA219 연결 실패");
    while (1);
  }

  Serial.println("INA219 연결 성공");
  Serial.println("m: 측정 시작 / s: 측정 중지");
  BT.println("INA219 연결 성공");
  BT.println("m: 측정 시작 / s: 측정 중지");
}

void loop() {
  if (Serial.available() > 0) processCommand(Serial.read());
  if (BT.available() > 0)     processCommand(BT.read());

  if (measurementEnabled) {
    unsigned long now = millis();
    if (now - previousMillis >= measurementInterval) {
      previousMillis = now;
      printMeasurement();
    }
  }
}

void processCommand(char command) {
  if (command == 'm' || command == 'M') {
    measurementEnabled = true;
    previousMillis = millis() - measurementInterval;
    Serial.println("측정값 출력을 시작합니다.");
    BT.println("측정값 출력을 시작합니다.");
  }
  else if (command == 's' || command == 'S') {
    measurementEnabled = false;
    Serial.println("측정값 출력을 중지합니다.");
    BT.println("측정값 출력을 중지합니다.");
  }
}

void printMeasurement() {
  float shunt_mV   = ina219.getShuntVoltage_mV();
  float bus_V      = ina219.getBusVoltage_V();
  float current_mA = ina219.getCurrent_mA();
  float power_mW   = ina219.getPower_mW();
  float supply_V   = bus_V + (shunt_mV / 1000.0);

  // USB 시리얼 — 사람이 읽는 형식
  Serial.println("===== INA219 측정값 =====");
  Serial.print("Bus Voltage: ");    Serial.print(bus_V, 2);      Serial.println(" V");
  Serial.print("Supply Voltage: "); Serial.print(supply_V, 2);   Serial.println(" V");
  Serial.print("Current: ");        Serial.print(current_mA, 2); Serial.println(" mA");
  Serial.print("Power: ");          Serial.print(power_mW, 2);   Serial.println(" mW");
  Serial.println();

  // BT — Unity가 파싱하는 한 줄 포맷
  BT.print("PWR:");
  BT.print(bus_V, 3);    BT.print(",");
  BT.print(supply_V, 3); BT.print(",");
  BT.print(current_mA, 3); BT.print(",");
  BT.println(power_mW, 3);
}
