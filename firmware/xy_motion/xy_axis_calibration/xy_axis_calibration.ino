///변경 코드(조그모드등 여러가지 기능을 넣은 코드)///
// ===== [1번 코드] XY축 최대 스텝 수 측정 (캘리브레이션 전용) =====

struct Axis {
  int stepPin, dirPin, minSw, maxSw;
  bool dirMinus, dirPlus;
  const char* label;
};

// ---- 모터 핀 ----
#define X_STEP 6
#define X_DIR  7
#define X_EN   8
#define Y_STEP 5
#define Y_DIR  4
#define Y_EN   12

// ---- 리미트 스위치 ----
#define X_MIN_SW 9
#define X_MAX_SW 11
#define Y_MIN_SW 10
#define Y_MAX_SW 3

Axis xAxis = {X_STEP, X_DIR, X_MIN_SW, X_MAX_SW, HIGH, LOW, "X"};
Axis yAxis = {Y_STEP, Y_DIR, Y_MIN_SW, Y_MAX_SW, HIGH, LOW, "Y"};

const unsigned int STEP_HALF_US = 500;
const int RUNS = 3;
const int BACKOFF_STEPS = 200;

long xMaxArr[RUNS], yMaxArr[RUNS];

void stepOnce(int stepPin) {
  digitalWrite(stepPin, HIGH); delayMicroseconds(STEP_HALF_US);
  digitalWrite(stepPin, LOW);  delayMicroseconds(STEP_HALF_US);
}

void checkStop() {
  if (Serial.available()) {
    if (tolower(Serial.read()) == 's') {
      Serial.println(F("\n!! 비상 정지 !!"));
      digitalWrite(X_EN, HIGH); digitalWrite(Y_EN, HIGH);
      while (true) delay(1000);
    }
  }
}

long calibrateAxis(Axis &ax) {
  Serial.print(F("  ")); Serial.print(ax.label); Serial.println(F("축 (-) 원점 탐색 중..."));
  digitalWrite(ax.dirPin, ax.dirMinus);
  delayMicroseconds(10);

  while (digitalRead(ax.minSw) != LOW) {
    checkStop();
    if (digitalRead(ax.maxSw) == LOW) { 
      Serial.print(F("    !! ")); Serial.print(ax.label); Serial.println(F("축 반대편 감지 - 방향 자동 반전"));
      bool tmp = ax.dirMinus; ax.dirMinus = ax.dirPlus; ax.dirPlus = tmp;
      digitalWrite(ax.dirPin, ax.dirMinus);
      delayMicroseconds(10);
      while (digitalRead(ax.maxSw) == LOW) { checkStop(); stepOnce(ax.stepPin); }
      for (int i = 0; i < BACKOFF_STEPS; i++) { checkStop(); stepOnce(ax.stepPin); }
      continue;
    }
    stepOnce(ax.stepPin);
  }
  Serial.println(F("    원점 도달")); delay(300);

  Serial.print(F("  ")); Serial.print(ax.label); Serial.println(F("축 (+) 끝점 탐색 중..."));
  digitalWrite(ax.dirPin, ax.dirPlus);
  delayMicroseconds(10);
  while (digitalRead(ax.minSw) == LOW) { checkStop(); stepOnce(ax.stepPin); }

  long steps = 0;
  while (digitalRead(ax.maxSw) != LOW) {
    checkStop(); stepOnce(ax.stepPin); steps++;
  }
  Serial.println(F("    끝점 도달 완료")); delay(200);

  digitalWrite(ax.dirPin, ax.dirMinus);
  delayMicroseconds(10);
  for (int i = 0; i < BACKOFF_STEPS; i++) { checkStop(); stepOnce(ax.stepPin); }
  delay(300);
  return steps;
}

void setup() {
  Serial.begin(9600);
  pinMode(X_STEP, OUTPUT); pinMode(X_DIR, OUTPUT); pinMode(X_EN, OUTPUT);
  pinMode(Y_STEP, OUTPUT); pinMode(Y_DIR, OUTPUT); pinMode(Y_EN, OUTPUT);
  pinMode(X_MIN_SW, INPUT_PULLUP); pinMode(X_MAX_SW, INPUT_PULLUP);
  pinMode(Y_MIN_SW, INPUT_PULLUP); pinMode(Y_MAX_SW, INPUT_PULLUP);

  digitalWrite(X_EN, HIGH); digitalWrite(Y_EN, HIGH);

  Serial.println(F("=== 스텝 수 측정(캘리브레이션) 프로그램 ==="));
  Serial.println(F("시작하려면 'm' + Enter (비상정지: 's')"));
  
  while (true) {
    if (Serial.available() && tolower(Serial.read()) == 'm') break;
    delay(10);
  }

  digitalWrite(X_EN, LOW); digitalWrite(Y_EN, LOW);
  Serial.println(F("\n>> 측정 시작 <<")); delay(500);

  for (int run = 0; run < RUNS; run++) {
    Serial.print(F("\n========== 사이클 ")); Serial.print(run + 1); Serial.println(F(" =========="));
    xMaxArr[run] = calibrateAxis(xAxis);
    yMaxArr[run] = calibrateAxis(yAxis);
  }

  long xSum = 0, ySum = 0;
  for (int i = 0; i < RUNS; i++) { xSum += xMaxArr[i]; ySum += yMaxArr[i]; }
  
  Serial.println(F("\n========== 측정 완료! 아래 숫자를 복사하세요 =========="));
  Serial.print(F("long X_MAX_STEPS = ")); Serial.print(xSum / RUNS); Serial.println(F(";"));
  Serial.print(F("long Y_MAX_STEPS = ")); Serial.print(ySum / RUNS); Serial.println(F(";"));
  Serial.println(F("======================================================="));
  
  digitalWrite(X_EN, HIGH); digitalWrite(Y_EN, HIGH);
}

void loop() {}
