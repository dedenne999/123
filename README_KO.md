# Stork Autopilot — Windows native build

Python is not required on the target PC. The Windows EXE is self-contained.

## Build without installing Python
This package includes a GitHub Actions workflow that compiles the Windows x64 executable on a hosted Windows runner.

1. Create a new GitHub repository.
2. Upload the contents of this folder (including `.github` and `StorkAutopilot`).
3. Open the repository's **Actions** tab.
4. Select **Build Windows EXE** and choose **Run workflow** (or push to `main`).
5. When complete, open the run and download the `StorkAutopilot-Windows-x64` artifact.
6. Extract `StorkAutopilot.exe` and run it on Windows 10/11 x64. No Python or .NET install is needed.

## Use
1. Open the stork game in the browser and leave the game visible.
2. Run `StorkAutopilot.exe`.
3. Click **게임 화면 선택** and drag over the full game canvas.
4. With the stork upright, click **중립 자세 보정**.
5. Click **자동 조작 시작**. You have 7 seconds to click the game.
6. Press Space in the game if it has not started. Press ESC or **긴급 정지** to stop.

## Notes
- The head detector uses OpenCV circle detection and inner/outer contrast; this is an experimental first pass, not a proven game solver.
- If the left/right direction is reversed, swap the two key selections in the app.
- The first test should be short and supervised. Detection failures release both arrow keys.
- The app captures the selected screen rectangle and sends actual Windows arrow-key events.
