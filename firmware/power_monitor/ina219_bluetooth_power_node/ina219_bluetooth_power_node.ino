// 아두이노 나노: INA219 전력 센서 노드 (스트리밍 + 쿼리 통합판)
// 블루투스: D2=RX(모듈 TXD), D3=TX(모듈 RXD)
//
// 프로토콜:
//   'm' → 스트리밍 시작: "전압V,전류mA,전력mW\n" 을 STREAM_MS 간격으로 연속 전송
//   's' → 스트리밍 중지
//   'R' → 단발 쿼리: 10회 평균한 "P <전압V>,<전류mA>,<전력mW>\n" 응답 (정밀정렬 스캔용, 스트리밍 중엔 무시)
//
// 스트리밍 데이터 형식 (CSV, 파이썬 파싱용):
//   5.02,1450.50,7285.6

#include <Wire.h>
#include <Adafruit_INA219.h>
#include <SoftwareSerial.h>

Adafruit_INA219 ina219;        // I2C: SDA=A4, SCL=A5
SoftwareSerial bt(2, 3);       // RX=D2, TX=D3

// 전송 주기(ms). 9600bps에서는 25ms(40Hz)가 안전 한계.
// HC-06을 AT+BAUD6(38400)로 올리면 10ms(100Hz)까지 가능 — 아래 BT_BAUD도 함께 변경.
const unsigned long STREAM_MS = 25;
const long BT_BAUD = 9600;

bool streaming = false;
bool outToBT = true;           // 출력 채널: 명령이 들어온 쪽으로 응답
unsigned long lastSend = 0;

void setup() {
  bt.begin(BT_BAUD);
  Serial.begin(9600);          // USB 디버그
  if (!ina219.begin()) {
    bt.println("ERR INA219");
    Serial.println("ERR INA219");
    while (1);
  }
  bt.println("READY");
}

void replyAvgVIP(Stream &out) {
  float sumV = 0, sumI = 0, sumP = 0;
  for (int i = 0; i < 10; i++) {
    sumV += ina219.getBusVoltage_V();
    sumI += ina219.getCurrent_mA();
    sumP += ina219.getPower_mW();
    delay(5);
  }
  out.print("P ");
  out.print(sumV / 10.0, 2); out.print(',');
  out.print(sumI / 10.0, 1); out.print(',');
  out.println(sumP / 10.0, 1);
}

void handleCommand(char c, bool fromBT) {
  if (c == 'm') {
    streaming = true;
    outToBT = fromBT;          // 명령 보낸 채널로 출력
    lastSend = 0;              // 즉시 첫 샘플 전송
  } else if (c == 's') {
    streaming = false;
  } else if (c == 'R' && !streaming) {
    Stream &out = fromBT ? (Stream&)bt : (Stream&)Serial;
    replyAvgVIP(out);
  }
}

void loop() {
  // 명령 수신 (블루투스/USB 어느 쪽이든 가능)
  while (bt.available())     handleCommand(bt.read(), true);
  while (Serial.available()) handleCommand(Serial.read(), false);

  // 스트리밍 모드: 일정 주기로 V, I, P 전송 (명령이 들어온 채널로)
  if (streaming && millis() - lastSend >= STREAM_MS) {
    lastSend = millis();

    float busV    = ina219.getBusVoltage_V();   // 수신부 출력(부하측) 전압 [V]
    float curr_mA = ina219.getCurrent_mA();     // 전류 [mA]
    float pwr_mW  = ina219.getPower_mW();       // 전력 [mW]

    Stream &out = outToBT ? (Stream&)bt : (Stream&)Serial;
    out.print(busV, 2);   out.print(',');
    out.print(curr_mA, 1); out.print(',');
    out.println(pwr_mW, 1);
  }
}
