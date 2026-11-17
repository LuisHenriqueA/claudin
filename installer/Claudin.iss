; Instalador do Claudin (Inno Setup 6).
; Gere antes o executável: tools\publish.ps1  (ou use tools\build-installer.ps1, que faz tudo)
;
; - Instala por usuário, sem pedir administrador: tudo do Claudin vive no perfil do usuário (HKCU, %LOCALAPPDATA%).
; - Não toca na instalação do Claude Desktop nem no protocolo claude://.
; - Na desinstalação, chama "Claudin.exe --uninstall": restaura o registro (início com o Windows) e pergunta se apaga
;   as pastas de perfil. A pasta padrão do Claude (%APPDATA%\Claude) nunca é apagada.

#define AppName "Claudin"
#define AppExe "Claudin.exe"
#define SrcExe "..\artifacts\publish\Claudin.exe"
#define AppVersion GetVersionNumbersString(SrcExe)

[Setup]
AppId={{9D3F6A2E-5B1C-4C8E-A7D4-2F1B0C6E8A31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Claudin (projeto pessoal)
AppComments=Alterna entre duas contas do Claude Desktop numa única janela.
DefaultDirName={localappdata}\Programs\Claudin
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\artifacts\installer
OutputBaseFilename=Claudin-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\Claudin\Assets\Claudin.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
; O Claudin cria este mutex enquanto roda: instalador e desinstalador pedem para fechá-lo antes.
AppMutex=Claudin.AppMutex
CloseApplications=yes
ShowLanguageDialog=no

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "Criar atalho na área de trabalho"; Flags: unchecked

[Files]
Source: "{#SrcExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Claudin"; Filename: "{app}\{#AppExe}"; Comment: "Alternar entre contas do Claude Desktop"
Name: "{autodesktop}\Claudin"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Abrir o Claudin"; Flags: nowait postinstall skipifsilent

[Code]
// Roda antes de remover os arquivos, enquanto Claudin.exe ainda existe.
// Silenciosa: só restaura o registro. Interativa: também pergunta sobre as pastas de perfil.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  Exe, Params: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    Exe := ExpandConstant('{app}\{#AppExe}');
    if FileExists(Exe) then
    begin
      if UninstallSilent then
        Params := '--uninstall --quiet'
      else
        Params := '--uninstall';
      if not Exec(Exe, Params, '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode) then
        Log('Claudin --uninstall não pôde ser executado: ' + SysErrorMessage(ResultCode));
    end;
  end;
end;
