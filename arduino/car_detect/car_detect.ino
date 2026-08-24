// ==========================================
// 초음파센서 6개 차량 감지 - 간단 버전
// Arduino UNO
//
// 차량 1 : TRIG D2  / ECHO D3
// 차량 2 : TRIG D4  / ECHO D5
// 차량 3 : TRIG D6  / ECHO D7
// 차량 4 : TRIG D8  / ECHO D9
// 차량 5 : TRIG D10 / ECHO D11
// 차량 6 : TRIG D12 / ECHO D13
//
// + 입력 → 감지 시작
// - 입력 → 감지 정지
//
// 10cm 이하 = O
// 10cm 초과 = X
// ==========================================


// 초음파센서 핀
const int trigPins[6] = {
  2, 4, 6, 8, 10, 12
};

const int echoPins[6] = {
  3, 5, 7, 9, 11, 13
};


// 차량 감지 기준 거리
const float CAR_DISTANCE = 10.0;


// 측정 상태
bool measurementEnabled = false;


void setup() {

  Serial.begin(9600);

  // 센서 핀 설정
  for (int i = 0; i < 6; i++) {

    pinMode(trigPins[i], OUTPUT);
    pinMode(echoPins[i], INPUT);

    digitalWrite(trigPins[i], LOW);
  }

  Serial.println("====================");
  Serial.println(" PARKING SENSOR READY");
  Serial.println("====================");
  Serial.println("+ : START");
  Serial.println("- : STOP");
}


void loop() {

  // 명령 확인
  if (Serial.available() > 0) {

    char command = Serial.read();

    // 측정 시작
    if (command == '+') {

      measurementEnabled = true;

      Serial.println("START");
    }

    // 측정 정지
    else if (command == '-') {

      measurementEnabled = false;

      Serial.println("STOP");
    }
  }


  // 측정 중
  if (measurementEnabled) {

    checkParking();

    delay(500);
  }
}


// ==========================================
// 차량 1~6 감지
// ==========================================
void checkParking() {

  for (int i = 0; i < 6; i++) {

    float distance = getDistance(
      trigPins[i],
      echoPins[i]
    );


    Serial.print(i + 1);
    Serial.print(" : ");


    // 10cm 이하이면 차량 있음
    if (distance > 0 &&
        distance <= CAR_DISTANCE) {

      Serial.println("O");
    }

    // 그 외에는 차량 없음
    else {

      Serial.println("X");
    }


    // 센서끼리 초음파 간섭 방지
    delay(60);
  }


  Serial.println("--------------------");
}


// ==========================================
// 거리 측정
// ==========================================
float getDistance(int trigPin, int echoPin) {

  digitalWrite(trigPin, LOW);
  delayMicroseconds(2);

  digitalWrite(trigPin, HIGH);
  delayMicroseconds(10);

  digitalWrite(trigPin, LOW);


  unsigned long duration =
    pulseIn(echoPin, HIGH, 30000);


  // 측정 실패
  if (duration == 0) {

    return -1;
  }


  // cm 계산
  float distance =
    duration * 0.0343 / 2.0;


  return distance;
}
