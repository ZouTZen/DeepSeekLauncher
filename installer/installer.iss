; DeepSeek Harness Launcher — Windows installer
; Build: ISCC.exe installer.iss  (produces setup.exe in this directory)

#define MyAppName "DeepSeek Harness Launcher"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "DeepSeek"
#define MyAppExeName "DshDesktop.exe"

[Setup]
AppId={{A8E7D6C5-B4A3-4921-8F7E-6D5C4B3A2910}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\DeepSeekHarnessLauncher
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Elevated so the node MSI and WebView2 runtime can be installed machine-wide.
PrivilegesRequired=admin
OutputDir=.
OutputBaseFilename=DeepSeekHarness-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Hide the "close after install" confusing option, keep it minimal.
DisableFinishedPage=no
UninstallDisplayName={#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}

[Languages]
Name: "chinesesimp"; MessagesFile: "ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "payload\DshDesktop.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "payload\Microsoft.Web.WebView2.Core.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "payload\Microsoft.Web.WebView2.WinForms.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "payload\WebView2Loader.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "payload\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "payload\setup-deps.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务:"; Flags: checkedonce

[Icons]
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"

[Run]
; Stage 1 (elevated): detect/install Node.js LTS + ensure WebView2 Runtime.
Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -NoProfile -File ""{app}\setup-deps.ps1"" -Stage node"; StatusMsg: "正在检测/安装 Node.js 与 WebView2 Runtime ..."; Flags: waituntilterminated runhidden
; Stage 2 (original user): npm install -g @deepseek-ai/dsh (keeps the global
; prefix in the real user's %APPDATA%\npm, which the launcher resolves).
; Skipped when the user already has harness (npm global or git clone).
Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -NoProfile -File ""{app}\setup-deps.ps1"" -Stage harness"; StatusMsg: "正在安装 harness (npm install -g @deepseek-ai/dsh),可能需要几分钟 ..."; Flags: waituntilterminated runhidden runasoriginaluser; Check: ShouldInstallHarness

[UninstallRun]
; Remove the npm-installed harness alongside the launcher. `exit 0` makes it a
; harmless no-op when harness was never npm-installed (e.g. a git clone).
Filename: "powershell.exe"; Parameters: "-ExecutionPolicy Bypass -NoProfile -Command ""npm uninstall -g @deepseek-ai/dsh; exit 0"""; Flags: runhidden waituntilterminated; RunOnceId: "UninstallHarness"

[Code]
var
  HarnessPage: TInputOptionWizardPage;

procedure InitializeWizard();
begin
  HarnessPage := CreateInputOptionPage(
    wpWelcome,
    'Harness 安装状态',
    '你是否已经安装了 DeepSeek Harness?',
    '已安装则直接安装桌面启动器(跳过 harness 自动安装);未安装则由安装器通过 npm 自动安装。',
    True, False);
  HarnessPage.Add('是,已通过 npx 安装 (npx @deepseek-ai/dsh web)');
  HarnessPage.Add('是,已通过 npm 全局安装 (npm install -g @deepseek-ai/dsh)');
  HarnessPage.Add('是,已通过 git clone 安装 (仓库内有 apps/cli/lib/bin.js)');
  HarnessPage.Add('否,尚未安装');
  HarnessPage.Values[0] := True;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpSelectDir then
  begin
    if HarnessPage.SelectedValueIndex = 2 then
      WizardForm.SelectDirLabel.Caption := '请选择安装位置。重要:请把启动器安装到你的 harness 仓库目录下(例如 D:\deepseek-harness),这样启动器才能直接定位到 harness。若安装到其他位置,启动器可能无法自动找到你的 harness。'
    else
      WizardForm.SelectDirLabel.Caption := '请选择安装位置。';
  end;
end;

function ShouldInstallHarness(): Boolean;
begin
  Result := (HarnessPage.SelectedValueIndex = 3);
end;
