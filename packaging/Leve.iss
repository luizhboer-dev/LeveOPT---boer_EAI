; Instalador do Leve (Inno Setup 6). Gerado por packaging\build-installer.ps1, que passa /DAppVersion.
; Instala em Program Files: pasta protegida, onde só administrador grava. Isso impede que alguém troque o
; Leve.exe/Leve.Agent.exe ou plante DLLs ao lado de um programa que roda como administrador.

#ifndef AppVersion
  #define AppVersion "1.2.0"
#endif

[Setup]
AppId={{8E7C3D2A-6B1F-4C59-9A0E-5F2D7B1C4E90}
AppName=LeveOPT
AppVersion={#AppVersion}
AppVerName=LeveOPT {#AppVersion}
AppPublisher=boer_eAI (distribuição entre amigos)
DefaultDirName={autopf}\LeveOPT
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=out
OutputBaseFilename=LeveOPT-Setup-{#AppVersion}
SetupIconFile=..\assets\leveopt.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
InfoBeforeFile=LEIA-ME.txt
UninstallDisplayName=LeveOPT {#AppVersion}
UninstallDisplayIcon={app}\Leve.exe
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; Flags: unchecked

[Files]
Source: "out\app\Leve.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "out\app\Leve.Agent.exe"; DestDir: "{app}"; Flags: ignoreversion
; Opcional: coloque o PresentMon-2.x.y-x64.exe em packaging\tools\ antes de gerar o instalador.
Source: "tools\PresentMon-*-x64.exe"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "LEIA-ME.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\LeveOPT"; Filename: "{app}\Leve.exe"
Name: "{autoprograms}\LeveOPT - LEIA-ME"; Filename: "{app}\LEIA-ME.txt"
Name: "{autodesktop}\LeveOPT"; Filename: "{app}\Leve.exe"; Tasks: desktopicon

[Run]
; runasoriginaluser: o Leve abre (com o UAC dele) na conta de quem instalou, para os ajustes de usuário irem para a conta certa.
Filename: "{app}\Leve.exe"; Description: "Abrir o LeveOPT agora"; Flags: nowait postinstall skipifsilent shellexec runasoriginaluser

[UninstallRun]
; Para o agente e apaga a tarefa sob demanda que o inicia sem elevação.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM Leve.Agent.exe"; Flags: runhidden; RunOnceId: "StopAgent"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""\Leve\Agent"" /F"; Flags: runhidden; RunOnceId: "DeleteAgentTask"

[Code]
const
  DesktopRuntimeKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  DotNetDownload = 'https://dotnet.microsoft.com/download/dotnet/10.0';

function IsDesktopRuntime10Installed: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(HKLM64, DesktopRuntimeKey, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Copy(Names[I], 1, 3) = '10.' then
      begin
        Result := True;
        Exit;
      end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep = ssPostInstall) and not IsDesktopRuntime10Installed then
  begin
    WizardForm.StatusLabel.Caption := 'Instalando o .NET Desktop Runtime 10 (necessário para o Leve)...';
    Exec('winget.exe',
      'install --id Microsoft.DotNet.DesktopRuntime.10 -e --silent --accept-package-agreements --accept-source-agreements',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

    if not IsDesktopRuntime10Installed then
    begin
      MsgBox('Não foi possível instalar o .NET Desktop Runtime 10 automaticamente.' + #13#10 + #13#10 +
        'Vou abrir a página de download: baixe "Windows Desktop Runtime" (x64) e instale antes de abrir o Leve.',
        mbInformation, MB_OK);
      ShellExec('open', DotNetDownload, '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
    end;
  end;
end;

function InitializeUninstall: Boolean;
begin
  Result := MsgBox('Antes de desinstalar:' + #13#10 + #13#10 +
    'Desinstalar NÃO desfaz os ajustes do Leve. Se quiser o Windows exatamente como era antes, ' +
    'cancele, abra o Leve e desfaça os lotes (tecla u, ou > snapshots > desfazer).' + #13#10 + #13#10 +
    'Continuar a desinstalação?', mbConfirmation, MB_YESNO) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    // Sem o executável, a entrada de inicialização do agente ficaria apontando para o nada.
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'LeveAgent');
end;
