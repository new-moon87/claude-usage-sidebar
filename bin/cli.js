#!/usr/bin/env node
"use strict";

const fs = require("fs");
const path = require("path");
const os = require("os");
const { execFileSync, spawn, spawnSync } = require("child_process");

const APP_NAME = "ClaudeSidebar";
const EXE_NAME = "ClaudeSidebar.exe";
const SRC_DIR = path.join(__dirname, "..", "dist");
const TARGET_DIR = path.join(
  process.env.LOCALAPPDATA || path.join(os.homedir(), "AppData", "Local"),
  APP_NAME
);
const TARGET_EXE = path.join(TARGET_DIR, EXE_NAME);

function fail(msg) {
  console.error("\n  " + msg + "\n");
  process.exit(1);
}

function requireWindows() {
  if (process.platform !== "win32") {
    fail("이 프로그램은 Windows 전용입니다. (현재: " + process.platform + ")");
  }
}

// .NET 8 Desktop Runtime이 있어야 실행된다.
function checkDotnet() {
  let out = "";
  try {
    out = execFileSync("dotnet", ["--list-runtimes"], { encoding: "utf8" });
  } catch {
    return false;
  }
  return /Microsoft\.WindowsDesktop\.App 8\./.test(out);
}

function warnDotnet() {
  console.log("");
  console.log("  .NET 8 Desktop Runtime을 찾지 못했습니다. 아래 명령으로 설치해 주세요:");
  console.log("");
  console.log("    winget install Microsoft.DotNet.DesktopRuntime.8");
  console.log("");
  console.log("  설치 후 다시 실행하면 됩니다.");
  console.log("");
}

function stopRunning() {
  spawnSync("taskkill", ["/IM", EXE_NAME, "/F"], { stdio: "ignore" });
}

function launch() {
  const child = spawn(TARGET_EXE, [], { detached: true, stdio: "ignore" });
  child.unref();
}

function install() {
  requireWindows();
  if (!fs.existsSync(SRC_DIR)) fail("빌드 산출물(dist)을 찾을 수 없습니다.");

  const hasDotnet = checkDotnet();

  stopRunning();
  fs.mkdirSync(TARGET_DIR, { recursive: true });
  for (const name of fs.readdirSync(SRC_DIR)) {
    fs.copyFileSync(path.join(SRC_DIR, name), path.join(TARGET_DIR, name));
  }
  console.log("\n  설치 위치: " + TARGET_DIR);

  if (!hasDotnet) {
    warnDotnet();
    return;
  }

  launch();
  console.log("  사이드바를 실행했습니다. 트레이 아이콘에서 설정할 수 있습니다.");
  console.log("  Claude Code 데스크톱 앱을 켜면 화면 오른쪽에 나타납니다.");
  console.log("  (Windows 시작 시 자동 실행은 앱이 스스로 등록합니다.)\n");
}

function uninstall() {
  requireWindows();
  stopRunning();

  // 자동 시작 등록 해제
  spawnSync(
    "reg",
    ["delete", "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run", "/v", APP_NAME, "/f"],
    { stdio: "ignore" }
  );

  if (fs.existsSync(TARGET_DIR)) {
    fs.rmSync(TARGET_DIR, { recursive: true, force: true });
  }
  console.log("\n  제거했습니다. 설정과 로그는 %APPDATA%\\" + APP_NAME + " 에 남아 있습니다.");
  console.log("  완전히 지우려면 그 폴더도 삭제하세요.\n");
}

function start() {
  requireWindows();
  if (!fs.existsSync(TARGET_EXE)) fail("설치되어 있지 않습니다. 먼저 install 을 실행하세요.");
  if (!checkDotnet()) return warnDotnet();
  stopRunning();
  launch();
  console.log("\n  사이드바를 실행했습니다.\n");
}

function help() {
  console.log(`
  claude-usage-sidebar — Claude Code 사용량 사이드바

  사용법:
    npx github:new-moon87/claude-usage-sidebar install     설치 후 실행
    npx github:new-moon87/claude-usage-sidebar start       이미 설치된 것을 다시 실행
    npx github:new-moon87/claude-usage-sidebar uninstall   제거
`);
}

const cmd = (process.argv[2] || "").toLowerCase();
if (cmd === "install") install();
else if (cmd === "uninstall" || cmd === "remove") uninstall();
else if (cmd === "start" || cmd === "run") start();
else help();
