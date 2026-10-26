@echo off
rem Ekran kasy w trybie kiosku (pełny ekran, bez pasków przeglądarki)
start "" msedge --kiosk http://localhost:5080 --edge-kiosk-type=fullscreen --no-first-run --disable-pinch --overscroll-history-navigation=0 --autoplay-policy=no-user-gesture-required
