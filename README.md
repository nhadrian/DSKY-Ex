External CMC DSKY for ReEntry CM/LM</br>
Compiled in .NET Framework 8.0</br>
by NHAdrian</br>
</br>
This project uses fonts from the DSKY-FONTS project @ https://github.com/ehdorrii/dsky-fonts</br>
This project uses some code from the ReEntryUDP example project @ https://github.com/ReentryGame/ReentryUDP</br>
This project uses some code and assets from the CMC-Ex project @ https://github.com/DevLAN-org/CMC-Ex</br>

Key features:
- works for both CM and LM (the app will always show the active DSKY, annunciator layout will vary according)
- touch input friendly
- stand-alone executable, no need to install .NET Framework 8.0

Instructions:</br>
1. Run ReEntry game and enable json output in settings, suggested refresh rate is 10Hz or more.</br>
2. Then fully load into your mission.</br>
3. Run the DSKY-Ex.exe application. 
4. Hit the "POWER" icon in the bottom right corner of the window to begin reading the ReEntry JSON output. The white power button indicates that json file reading is working. The application will always show and iteract with the active computer (CMC or LMC)</br>
5. If needed, use the darkmode icon to toggle a dark setting.</br>
6. All DSKY keys function over ReEntry UDP cmds.</br>
7. To close the application(s) click on the close icon at the top right corner (will appear on mouse over) or use Alt+F4 or right-click close from the taskbar icon.

AGC layout (day/night):
<img width="1208" height="1336" alt="Screenshot 2026-01-11 180005" src="https://github.com/user-attachments/assets/9d904cfe-6f2d-49da-8272-8403dce1ab23" />
<img width="1231" height="1351" alt="Screenshot 2026-01-11 180010" src="https://github.com/user-attachments/assets/ccdb8eb2-40b7-4894-967e-eef6e1a65c35" />

LGC layout (day/night):
<img width="1220" height="1343" alt="Screenshot 2026-01-11 175937" src="https://github.com/user-attachments/assets/dbf0f306-c903-4cfc-8b0c-1ced5770d67f" />
<img width="1221" height="1341" alt="Screenshot 2026-01-11 175943" src="https://github.com/user-attachments/assets/25c8232e-aa8b-4bc7-a795-c22db1566d26" />
