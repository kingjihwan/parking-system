// 오렌지보드: 스텝모터 이동 실행기
// 프로토콜: "M<n>" 상대이동(부호O) / "G<s>" 절대이동 / "Z" 현재위치 0 설정
// 이동 완료 후 "OK <절대위치>\n" 응답

// X축 핀 (DFRobot Dual Bipolar Stepper Shield 기준)
const int STEP_PIN = 6;
const int DIR_PIN  = 7;
const int EN_PIN   = 8;

const unsigned int STEP_DELAY_US = 800;  // 스텝 간격(속도) — 느리게 스캔하려면 키우기
long currentPos = 0;                     // 절대 스텝 카운터

String buf = "";

void setup() {
  pinMode(STEP_PIN, OUTPUT);
  pinMode(DIR_PIN, OUTPUT);
  pinMode(EN_PIN, OUTPUT);
  digitalWrite(EN_PIN, LOW);   // 드라이버 활성 (DRV8825: LOW=enable)
  Serial.begin(9600);
}

void moveSteps(long n) {
  if (n == 0) return;
  digitalWrite(DIR_PIN, n > 0 ? HIGH : LOW);
  long cnt = abs(n);
  for (long i = 0; i < cnt; i++) {
    digitalWrite(STEP_PIN, HIGH);
    delayMicroseconds(STEP_DELAY_US);
    digitalWrite(STEP_PIN, LOW);
    delayMicroseconds(STEP_DELAY_US);
  }
  currentPos += n;
}

void handleCmd(String cmd) {
  cmd.trim();
  if (cmd.length() == 0) return;
  char c = cmd.charAt(0);
  if (c == 'M') {
    moveSteps(cmd.substring(1).toInt());
  } else if (c == 'G') {
    long target = cmd.substring(1).toInt();
    moveSteps(target - currentPos);
  } else if (c == 'Z') {
    currentPos = 0;
  } else {
    Serial.println("ERR");
    return;
  }
  Serial.print("OK ");
  Serial.println(currentPos);
}

void loop() {
  while (Serial.available()) {
    char ch = Serial.read();
    if (ch == '\n') { handleCmd(buf); buf = ""; }
    else buf += ch;
  }
}
