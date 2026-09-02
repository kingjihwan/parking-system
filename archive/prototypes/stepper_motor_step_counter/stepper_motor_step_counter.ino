//y축//
// ===== 핀 번호 설정 =====
int enablePin = 12;   // 모터 ON/OFF 제어 핀
int stepPin = 5;      // 스텝 신호 출력 핀
int dirPin = 4;       // 회전 방향 제어 핀

// ===== 상태 변수 =====
bool isRunning = false; // 현재 모터가 돌고 있는지 저장
long stepCount = 0;     // 지금까지 이동한 총 스텝 수 저장

void setup() {

  // 컴퓨터와 아두이노 통신 시작
  Serial.begin(9600);

  // 핀을 출력 모드로 설정
  pinMode(enablePin, OUTPUT);
  pinMode(stepPin, OUTPUT);
  pinMode(dirPin, OUTPUT);

  // 처음에는 모터 정지 상태
  // HIGH = Disable
  // LOW  = Enable
  digitalWrite(enablePin, HIGH);

  // 방향 설정
  // HIGH와 LOW를 바꾸면 반대 방향 회전
  digitalWrite(dirPin, HIGH);

  // 시리얼 모니터에 안내문 출력
  Serial.println("1 = START");
  Serial.println("0 = STOP");
}

void loop() {

  // 컴퓨터로부터 데이터가 들어왔는지 확인
  if (Serial.available()) {

    // 입력된 문자 1개 읽기
    char cmd = Serial.read();

    // ===== START 명령 =====
    if (cmd == '1') {

      // 스텝 카운트 초기화
      stepCount = 0;

      // 회전 시작 플래그
      isRunning = true;

      // 드라이버 활성화
      digitalWrite(enablePin, LOW);

      Serial.println("START");
    }

    // ===== STOP 명령 =====
    if (cmd == '0') {

      // 회전 정지
      isRunning = false;

      // 드라이버 비활성화
      digitalWrite(enablePin, HIGH);

      // 현재까지 이동한 스텝 수 출력
      Serial.print("STOP! 총 스텝 수 = ");
      Serial.println(stepCount);
    }
  }

  // ===== 모터 회전 부분 =====
  if (isRunning) {

    // STEP HIGH
    // 드라이버가 스텝 신호를 인식
    digitalWrite(stepPin, HIGH);

    // 펄스 폭
    delayMicroseconds(1200);

    // STEP LOW
    digitalWrite(stepPin, LOW);

    // 다음 스텝 전까지 대기
    delayMicroseconds(1200);

    // 스텝 수 1 증가
    stepCount++;
  }
}
//x축//
// ===== 핀 번호 설정 =====
int enablePin = 8;   // 모터 ON/OFF 제어 핀
int stepPin = 6;      // 스텝 신호 출력 핀
int dirPin = 7;       // 회전 방향 제어 핀

// ===== 상태 변수 =====
bool isRunning = false; // 현재 모터가 돌고 있는지 저장
long stepCount = 0;     // 지금까지 이동한 총 스텝 수 저장

void setup() {

  // 컴퓨터와 아두이노 통신 시작
  Serial.begin(9600);

  // 핀을 출력 모드로 설정
  pinMode(enablePin, OUTPUT);
  pinMode(stepPin, OUTPUT);
  pinMode(dirPin, OUTPUT);

  // 처음에는 모터 정지 상태
  // HIGH = Disable
  // LOW  = Enable
  digitalWrite(enablePin, HIGH);

  // 방향 설정
  // HIGH와 LOW를 바꾸면 반대 방향 회전
  digitalWrite(dirPin, HIGH);

  // 시리얼 모니터에 안내문 출력
  Serial.println("1 = START");
  Serial.println("0 = STOP");
}

void loop() {

  // 컴퓨터로부터 데이터가 들어왔는지 확인
  if (Serial.available()) {

    // 입력된 문자 1개 읽기
    char cmd = Serial.read();

    // ===== START 명령 =====
    if (cmd == '1') {

      // 스텝 카운트 초기화
      stepCount = 0;

      // 회전 시작 플래그
      isRunning = true;

      // 드라이버 활성화
      digitalWrite(enablePin, LOW);

      Serial.println("START");
    }

    // ===== STOP 명령 =====
    if (cmd == '0') {

      // 회전 정지
      isRunning = false;

      // 드라이버 비활성화
      digitalWrite(enablePin, HIGH);

      // 현재까지 이동한 스텝 수 출력
      Serial.print("STOP! 총 스텝 수 = ");
      Serial.println(stepCount);
    }
  }

  // ===== 모터 회전 부분 =====
  if (isRunning) {

    // STEP HIGH
    // 드라이버가 스텝 신호를 인식
    digitalWrite(stepPin, HIGH);

    // 펄스 폭
    delayMicroseconds(1200);

    // STEP LOW
    digitalWrite(stepPin, LOW);

    // 다음 스텝 전까지 대기
    delayMicroseconds(1200);

    // 스텝 수 1 증가
    stepCount++;
  }
}
