[Version]
Class=IEXPRESS
SEDVersion=3

[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=1
HideExtractAnimation=0
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=Install DiscBox on this PC?
DisplayLicense=
FinishMessage=DiscBox setup finished.
TargetName=C:\Users\jpmmm\Desktop\cenas\DiscBox\release\DiscBoxSetup-0.1.0.exe
FriendlyName=DiscBox Setup
AppLaunched=install.cmd
PostInstallCmd=<None>
AdminQuietInstCmd=install.cmd
UserQuietInstCmd=install.cmd
SourceFiles=SourceFiles

[Strings]
FILE0="install.cmd"
FILE1="install.ps1"
FILE2="DiscBox_payload.zip"
FILE3="DiscBox.ico"

[SourceFiles]
SourceFiles0=C:\Users\jpmmm\Desktop\cenas\DiscBox\release\installer-work

[SourceFiles0]
%FILE0%=
%FILE1%=
%FILE2%=
%FILE3%=
