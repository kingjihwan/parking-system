// ===== 18구역 주차장 제어 (구역별 스텝수 고정 테이블) =====
// 각 주차 구역의 목표 스텝수를 실측/조정값으로 직접 고정.
//   - X축: A→C 로 갈수록 스텝 증가(반전 없음)
//   - Y축: 1→6 행으로 갈수록 스텝 감소(반전)
// 비율/스케일/보정(CORR) 계산 로직은 모두 제거하고,
// 아래 stepX[], stepY[] 테이블 값을 그대로 목표로 이동한다.

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
#define X_MIN_SW 9    // X축 원점
#define X_MAX_SW 11   // X축 끝점
#define Y_MIN_SW 10   // Y축 원점
#define Y_MAX_SW 3    // Y축 끝점

Axis xAxis = {X_STEP, X_DIR, X_MIN_SW, X_MAX_SW, HIGH, LOW, "X"};
Axis yAxis = {Y_STEP, Y_DIR, Y_MIN_SW, Y_MAX_SW, HIGH, LOW, "Y"};

// ---- 실측 전체 스텝 수 (범위 판정/클램프용) ----
long X_MAX_STEPS = 28178;
long Y_MAX_STEPS = 28926;

// ==========================================================
// ===== 구역별 고정 스텝 테이블 (여기만 수정하면 위치 조정) =====
// ==========================================================
const long stepX[3] = {3082, 14089, 25279};
const long stepY[6] = {27980, 22974, 17967, 10736, 5730, 723};
// ==========================================================

const int  FAST_DELAY    = 350;
const int  SLOW_DELAY    = 1000;
const long RAMP_STEPS    = 1500;
const int  BACKOFF_STEPS = 200;

// ---- 상태 변수 ----
long curX = 0, curY = 0;
int  lastColIdx = -1, lastRowIdx = -1;
volatile bool stopFlag = false;

// ---- [추가] 실시간 위치 전송 타이머 ----
unsigned long lastPosSendMs = 0;

// ---- [추가] Unity로 현재 스텝 위치 전송 ----
void sendPos() {
  Serial.print(F("POS:")); Serial.print(curX);
  Serial.print(F(",")); Serial.println(curY);
}

void stepOnce(int stepPin, int delayUs) {
  digitalWrite(stepPin, HIGH); delayMicroseconds(delayUs);
  digitalWrite(stepPin, LOW);  delayMicroseconds(delayUs);
}

void checkStopMoving() {
  if (Serial.available()) {
    char c = Serial.peek();
    if (c == 's' || c == 'S' || c == 'e' || c == 'E') {
      Serial.read(); stopFlag = true;
      Serial.println(F("\n>> 정지 명령 수신"));
    }
  }
}

void moveAxis(Axis &ax, long target, long &current) {
  long diff = target - current;
  if (diff == 0) return;

  bool movingNeg = (diff < 0);
  digitalWrite(ax.dirPin, movingNeg ? ax.dirMinus : ax.dirPlus);

  long steps = abs(diff);
  long dir   = movingNeg ? -1 : 1;

  for (long i = 0; i < steps; i++) {
    if (stopFlag) return;

    if (movingNeg && digitalRead(ax.minSw) == LOW) {
      current = 0; return;
    }
    if (!movingNeg && digitalRead(ax.maxSw) == LOW) {
      current = (ax.label[0] == 'X') ? X_MAX_STEPS : Y_MAX_STEPS; return;
    }

    int d;
    long remain = steps - i;
    if (steps <= RAMP_STEPS * 2)    d = SLOW_DELAY;
    else if (i < RAMP_STEPS)        d = SLOW_DELAY - (long)(SLOW_DELAY - FAST_DELAY) * i / RAMP_STEPS;
    else if (remain < RAMP_STEPS)   d = SLOW_DELAY - (long)(SLOW_DELAY - FAST_DELAY) * remain / RAMP_STEPS;
    else                             d = FAST_DELAY;

    stepOnce(ax.stepPin, d);
    current += dir;

    // [추가] 100ms마다 Unity에 현재 위치 전송
    unsigned long now = millis();
    if (now - lastPosSendMs >= 100UL) {
      lastPosSendMs = now;
      sendPos();
    }

    if ((i & 0x3F) == 0) checkStopMoving();
  }
}

bool moveTo(long tx, long ty) {
  if (tx < 0 || tx > X_MAX_STEPS || ty < 0 || ty > Y_MAX_STEPS) {
    Serial.println(F(">> 범위 이탈! 이동 취소")); return false;
  }
  moveAxis(yAxis, ty, curY);
  if (stopFlag) return false;
  moveAxis(xAxis, tx, curX);
  return !stopFlag;
}

void moveToCell(int col_idx, int row_idx) {
  if (col_idx < 0 || col_idx >= 3 || row_idx < 0 || row_idx >= 6) return;

  long tx = stepX[col_idx];
  long ty = stepY[row_idx];

  if (tx < 0) tx = 0; if (tx > X_MAX_STEPS) tx = X_MAX_STEPS;
  if (ty < 0) ty = 0; if (ty > Y_MAX_STEPS) ty = Y_MAX_STEPS;

  char colChar = 'A' + col_idx;
  int  rowNum  = row_idx + 1;

  Serial.print(F(">> [")); Serial.print(colChar); Serial.print(rowNum);
  Serial.print(F("] 구역 이동 (목표 X:")); Serial.print(tx);
  Serial.print(F(" Y:")); Serial.print(ty); Serial.println(F(")"));

  if (moveTo(tx, ty)) {
    lastColIdx = col_idx; lastRowIdx = row_idx;
    Serial.println(F(">> 이동 완료"));
    sendPos();  // [추가] 도착 확정 위치 전송
  }
}

void forceHome(Axis &ax, long &current) {
  digitalWrite(ax.dirPin, ax.dirMinus);
  while (digitalRead(ax.minSw) != LOW) {
    if (stopFlag) return;
    if (digitalRead(ax.maxSw) == LOW) {
      bool tmp = ax.dirMinus; ax.dirMinus = ax.dirPlus; ax.dirPlus = tmp;
      digitalWrite(ax.dirPin, ax.dirMinus);
      delay(100);
      while (digitalRead(ax.maxSw) == LOW) stepOnce(ax.stepPin, SLOW_DELAY);
    }
    stepOnce(ax.stepPin, SLOW_DELAY);
    if (Serial.available()) checkStopMoving();
  }
  digitalWrite(ax.dirPin, ax.dirPlus);
  for (int i = 0; i < BACKOFF_STEPS; i++) stepOnce(ax.stepPin, SLOW_DELAY);
  current = BACKOFF_STEPS;
}

void goHome() {
  Serial.println(F("\n>> 물리적 원점(X=0, Y=0) 복귀 시작"));
  stopFlag = false;
  forceHome(xAxis, curX);
  if (stopFlag) return;
  forceHome(yAxis, curY);
  if (!stopFlag) {
    lastColIdx = -1; lastRowIdx = -1;
    Serial.println(F(">> 원점 복귀 완료"));
    sendPos();  // [추가]
  }
}

void printStatus() {
  Serial.println(F("\n===== 현재 상태 ====="));
  Serial.print(F("물리적 좌표  X:")); Serial.print(curX); Serial.print(F(" Y:")); Serial.println(curY);
  if (lastColIdx >= 0) {
    Serial.print(F("주차 구역    ")); Serial.print((char)('A' + lastColIdx)); Serial.println(lastRowIdx + 1);
  } else {
    Serial.println(F("주차 구역    미확인(원점 또는 이동중)"));
  }
  Serial.println(F("====================="));
}

void jogAxis(Axis &ax, long steps, bool positive, long &current) {
  digitalWrite(ax.dirPin, positive ? ax.dirPlus : ax.dirMinus);
  long moved = 0; bool limitHit = false;
  for (long i = 0; i < steps; i++) {
    if (Serial.available() && (tolower(Serial.peek()) == 's')) { Serial.read(); break; }
    if (!positive && digitalRead(ax.minSw) == LOW) { limitHit = true; break; }
    if (positive  && digitalRead(ax.maxSw) == LOW) break;
    stepOnce(ax.stepPin, SLOW_DELAY); moved++;
  }
  if (limitHit) current = 0; else current += positive ? moved : -moved;
}

void jogMode() {
  Serial.println(F("\n===== 수동 조그 모드 ====="));
  Serial.println(F("사용법: x+100 / x-50 / y+200 / y-100  (숫자 생략시 100스텝), q=종료"));
  while (true) {
    if (!Serial.available()) continue;
    String cmd = Serial.readStringUntil('\n'); cmd.trim();
    if (cmd.length() == 0) continue;
    if (cmd == "q" || cmd == "Q") return;
    else if (cmd.startsWith("x") || cmd.startsWith("X") || cmd.startsWith("y") || cmd.startsWith("Y")) {
      char axis = tolower(cmd.charAt(0)); char sign = cmd.charAt(1);
      if (sign != '+' && sign != '-') continue;
      long n = (cmd.length() > 2) ? cmd.substring(2).toInt() : 100;
      if (n <= 0) n = 100;
      if (axis == 'x') jogAxis(xAxis, n, (sign == '+'), curX);
      else             jogAxis(yAxis, n, (sign == '+'), curY);
      Serial.print(F(">> 조그 후 좌표  X:")); Serial.print(curX);
      Serial.print(F(" Y:")); Serial.println(curY);
      sendPos();  // [추가]
    }
  }
}

void setup() {
  Serial.begin(9600);
  pinMode(X_STEP, OUTPUT); pinMode(X_DIR, OUTPUT); pinMode(X_EN, OUTPUT);
  pinMode(Y_STEP, OUTPUT); pinMode(Y_DIR, OUTPUT); pinMode(Y_EN, OUTPUT);
  pinMode(X_MIN_SW, INPUT_PULLUP); pinMode(X_MAX_SW, INPUT_PULLUP);
  pinMode(Y_MIN_SW, INPUT_PULLUP); pinMode(Y_MAX_SW, INPUT_PULLUP);

  digitalWrite(X_EN, LOW); digitalWrite(Y_EN, LOW);

  goHome();

  Serial.println(F("\n===== 18구역 제어 준비 완료 (원점 대기) ====="));
  Serial.println(F("이동할 구역 입력 (예: A1, C6)  |  h=원점 t=상태 j=조그 s=정지"));
}

void loop() {
  if (!Serial.available()) return;
  char firstChar = tolower(Serial.read());

  if      (firstChar == 'h') goHome();
  else if (firstChar == 'e') { stopFlag = false; goHome(); }
  else if (firstChar == 't') printStatus();
  else if (firstChar == 'j') jogMode();
  else if (firstChar == 's') { stopFlag = true; Serial.println(F(">> 즉시 정지 명령")); }
  else if (firstChar >= 'a' && firstChar <= 'c') {
    delay(20);
    if (Serial.available()) {
      char rowChar = Serial.read();
      int row_idx = rowChar - '1';
      int col_idx = (firstChar == 'a') ? 0 : (firstChar == 'b' ? 1 : 2);
      if (row_idx >= 0 && row_idx < 6) {
        stopFlag = false; moveToCell(col_idx, row_idx);
      } else { Serial.println(F(">> [오류] 잘못된 구역입니다. (예: A1 ~ C6)")); }
    }
  }
  while (Serial.available() && (Serial.peek() == '\n' || Serial.peek() == '\r' || Serial.peek() == ' ')) Serial.read();
}
