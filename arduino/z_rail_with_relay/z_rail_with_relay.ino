#include <SoftwareSerial.h>

// ==========================================
// HC-06 블루투스
// HC-06 TXD → Nano D9
// Nano D10 → 분압 → HC-06 RXD
// ==========================================
SoftwareSerial BT(9, 10);   // RX, TX


// ==========================================
// Z축 모터
// CNC Shield X축 슬롯 사용
// ==========================================
#define STEP_PIN 2
#define DIR_PIN  5
#define EN_PIN   8

#define UP_DIR   LOW
#define DOWN_DIR HIGH


// ==========================================
// 릴레이
// 릴레이 IN → Nano D11
// ==========================================
#define RELAY_PIN 11

// Active LOW 릴레이
#define RELAY_ON  LOW
#define RELAY_OFF HIGH


// ==========================================
// 모터 속도
// 숫자가 작을수록 빠름
// ==========================================
const unsigned int STEP_DELAY_US = 600;


// ==========================================
// 한 번 이동할 스텝 수
// ==========================================
const long MOVE_STEPS = 80000;


// ==========================================
// 모터 상태
//
//  0 = 정지
//  1 = 상승
// -1 = 하강
// ==========================================
int motorState = 0;


// ==========================================
// 남은 이동 스텝 수
// ==========================================
long remainingSteps = 0;


void setup() {

  Serial.begin(9600);
  BT.begin(9600);


  // 모터 핀
  pinMode(STEP_PIN, OUTPUT);
  pinMode(DIR_PIN, OUTPUT);
  pinMode(EN_PIN, OUTPUT);


  // 릴레이 핀
  pinMode(RELAY_PIN, OUTPUT);


  // 시작 상태
  digitalWrite(STEP_PIN, LOW);

  // 모터 비활성화
  digitalWrite(EN_PIN, HIGH);

  // 릴레이 OFF
  digitalWrite(RELAY_PIN, RELAY_OFF);


  Serial.println("========================");
  Serial.println(" Z AXIS + RELAY READY");
  Serial.println("========================");
  Serial.println("1 = UP 80000 STEPS");
  Serial.println("2 = DOWN 80000 STEPS");
  Serial.println("3 = RELAY ON");
  Serial.println("4 = RELAY OFF");
  Serial.println("s = EMERGENCY STOP");
}


void loop() {

  // ========================================
  // HC-06 명령 수신
  // ========================================
  if (BT.available() > 0) {

    char command = BT.read();


    // --------------------------------------
    // 1 : 80000스텝 상승
    // --------------------------------------
    if (command == '1') {

      motorState = 1;

      // 이동할 스텝 수 설정
      remainingSteps = MOVE_STEPS;

      // 상승 방향
      digitalWrite(DIR_PIN, UP_DIR);

      // 모터 활성화
      digitalWrite(EN_PIN, LOW);

      BT.println("Z UP 80000 START");
      Serial.println("Z UP 80000 START");
    }


    // --------------------------------------
    // 2 : 80000스텝 하강
    // --------------------------------------
    else if (command == '2') {

      motorState = -1;

      // 이동할 스텝 수 설정
      remainingSteps = MOVE_STEPS;

      // 하강 방향
      digitalWrite(DIR_PIN, DOWN_DIR);

      // 모터 활성화
      digitalWrite(EN_PIN, LOW);

      BT.println("Z DOWN 80000 START");
      Serial.println("Z DOWN 80000 START");
    }


    // --------------------------------------
    // 3 : 릴레이 ON
    // --------------------------------------
    else if (command == '3') {

      digitalWrite(RELAY_PIN, RELAY_ON);

      BT.println("RELAY ON");
      Serial.println("RELAY ON");
    }


    // --------------------------------------
    // 4 : 릴레이 OFF
    // --------------------------------------
    else if (command == '4') {

      digitalWrite(RELAY_PIN, RELAY_OFF);

      BT.println("RELAY OFF");
      Serial.println("RELAY OFF");
    }


    // --------------------------------------
    // s : Z축 즉시 정지
    // --------------------------------------
    else if (command == 's' || command == 'S') {

      motorState = 0;
      remainingSteps = 0;

      digitalWrite(STEP_PIN, LOW);
      digitalWrite(EN_PIN, HIGH);

      BT.println("Z STOP");
      Serial.println("Z STOP");
    }
  }


  // ========================================
  // Z축 스텝 이동
  // ========================================
  if (motorState != 0 && remainingSteps > 0) {

    // 1스텝 발생
    digitalWrite(STEP_PIN, HIGH);
    delayMicroseconds(STEP_DELAY_US);

    digitalWrite(STEP_PIN, LOW);
    delayMicroseconds(STEP_DELAY_US);

    // 남은 스텝 1 감소
    remainingSteps--;


    // ======================================
    // 80000스텝 이동 완료
    // ======================================
    if (remainingSteps == 0) {

      // 어떤 방향으로 움직였는지 저장
      int finishedDirection = motorState;

      // 모터 정지
      motorState = 0;

      digitalWrite(STEP_PIN, LOW);
      digitalWrite(EN_PIN, HIGH);


      // 상승 완료
      if (finishedDirection == 1) {

        BT.println("Z UP 80000 COMPLETE");
        Serial.println("Z UP 80000 COMPLETE");
      }


      // 하강 완료
      else if (finishedDirection == -1) {

        BT.println("Z DOWN 80000 COMPLETE");
        Serial.println("Z DOWN 80000 COMPLETE");
      }
    }
  }
}
