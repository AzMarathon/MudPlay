namespace MudPlay.Services.Update;

// Generates the tiny external helper that performs the actual install swap AFTER
// this process exits — the running executable and its loaded libraries can't be
// overwritten in place, so the app stages the new build, spawns one of these, and
// quits; the helper waits for the PID to die, swaps the files in, relaunches, and
// cleans up. On success it removes the staged download, the backup, AND itself — no
// update leftovers survive in temp.
//
// The helper works file by file: the new executable, put in under the RUNNING
// executable's name (a browser may have saved it as "MudPlay (1).exe"), and the
// list of the build's other files UpdatePlan wrote. In the install folder it writes
// only those files, its failure log, and its own temporary copies; it deletes only
// those temporary copies, the renamed-aside Windows executable, and, when a swap
// fails, a file it had just added. It never moves, mirrors, purges or deletes the
// folder itself: the folder may be Downloads or the Desktop. Files an older release
// left behind are the new build's to remove (RetiredFiles). Each file it replaces
// is first copied into the backup folder inside the staging folder in temp — never
// beside the install — and put back from there if a later step fails.
//
// Pure string generation.
// Every value reaches the helper through the environment (UpdateInstaller.
// SwapStartInfo), never its command line: cmd re-parses a command line, so a
// folder named "R&D" there would end the command at the "&" and run the rest. The
// list entries are held by UpdatePlan to plain names with no shell syntax in them.
// Either helper refuses to start, touching nothing, when a value it needs is
// missing or the staging folder isn't an update's own.
public static class SwapScriptBuilder
{
    // bash for Linux/macOS. Values from the environment: MUDPLAY_SWAP_PID, _NEW
    // (the extracted build), _NEWEXE (its executable), _DST (the install folder),
    // _EXE (the running executable), _STAGE (the work folder), _PROFILE, _RECONNECT,
    // _INSTALL (the list of the build's other files). The profile and reconnect
    // values may be empty — an update from a client with no named profile loaded,
    // or one that wasn't connected, relaunches with no argv.
    //
    // A file goes in by copying it beside its target under a temporary name, then
    // renaming it over the target: a rename replaces an executable another client is
    // still running, where writing into it fails, and no launch ever finds a
    // half-copied file.
    public static string BuildPosix() => """
        #!/usr/bin/env bash
        set -u
        PID="${MUDPLAY_SWAP_PID:-}"; NEW="${MUDPLAY_SWAP_NEW:-}"; NEWEXE="${MUDPLAY_SWAP_NEWEXE:-}"
        DST="${MUDPLAY_SWAP_DST:-}"; EXE="${MUDPLAY_SWAP_EXE:-}"; STAGE="${MUDPLAY_SWAP_STAGE:-}"
        PROFILE="${MUDPLAY_SWAP_PROFILE:-}"; RECONNECT="${MUDPLAY_SWAP_RECONNECT:-}"
        INSTALL="${MUDPLAY_SWAP_INSTALL:-}"

        # Refuse, touching nothing, unless every value is there and the staging
        # folder is an update's own: it is deleted at the end.
        if [ -z "$PID" ] || [ -z "$NEW" ] || [ -z "$NEWEXE" ] || [ -z "$DST" ] || [ -z "$EXE" ] \
           || [ -z "$STAGE" ] || [ -z "$INSTALL" ]; then exit 2; fi
        case "$STAGE" in *mudplay-update-*) ;; *) exit 2 ;; esac
        if [ ! -d "$DST" ] || [ ! -d "$STAGE" ] || [ ! -f "$INSTALL" ] || [ ! -f "$NEWEXE" ]; then exit 2; fi

        EXENAME="${EXE##*/}"
        BAK="$STAGE/backup"
        SAVED="$STAGE/backed-up.list"
        TOUCHED="$STAGE/touched.list"
        SUF=".mudplay-update-$$"
        LOG="${0%.*}.log"
        FAILLOG="${DST}/MudPlay-update-failed.log"
        log() { printf '[%s] %s\n' "$(date '+%F %T')" "$*" >>"$LOG"; }

        # Carry the session across the restart: the same character reopens, and a
        # client that was mid-session dials straight back in. Built as an array so a
        # profile name with spaces survives; the ${ARGS[@]+...} form is what keeps an
        # empty array from tripping `set -u`.
        ARGS=()
        [ -n "$PROFILE" ] && ARGS+=(--profile "$PROFILE")
        [ -n "$RECONNECT" ] && ARGS+=(--reconnect)

        # A failed swap leaves this step log beside the executable for the user to
        # attach to a bug report; the relaunched client points them at it. Never
        # written through a link or over a folder of that name.
        relaunch_old() {
          log "restarting the old build"
          if [ -L "$FAILLOG" ] || [ -d "$FAILLOG" ]; then
            log "not writing the failure log over the link or folder '$FAILLOG'"
          else
            cp -f "$LOG" "$FAILLOG" 2>/dev/null || true
          fi
          "$EXE" ${ARGS[@]+"${ARGS[@]}"} </dev/null >/dev/null 2>&1 &
          exit 1
        }

        # Before anything in the install folder has changed.
        abort() {
          log "FAILED: $1 - install untouched"
          rm -rf "$STAGE"
          relaunch_old
        }

        # Copy $1 beside DST/$2 under a temporary name, then rename it over DST/$2.
        put() {
          if [ -d "$DST/$2" ]; then log "'$2' is a folder"; return 1; fi
          if [ -e "$DST/$2$SUF" ] || [ -L "$DST/$2$SUF" ]; then log "'$2$SUF' is in the way"; return 1; fi
          if cp -pP -- "$1" "$DST/$2$SUF" 2>>"$LOG" && mv -f -- "$DST/$2$SUF" "$DST/$2" 2>>"$LOG"; then
            return 0
          fi
          rm -f -- "$DST/$2$SUF"
          return 1
        }

        # Undo every file this run touched: a backed-up one is put back, one that
        # wasn't there before the update is taken out again. When the record of what
        # was backed up can't be read, nothing is taken out and the backup is kept.
        # The backup is dropped only once everything is back.
        rollback() {
          log "FAILED: $1 - restoring the backup"
          local ok=1 f rc
          while IFS= read -r f || [ -n "$f" ]; do
            [ -n "$f" ] || continue
            grep -qxF -- "$f" "$SAVED"; rc=$?
            if [ "$rc" = 0 ]; then
              if put "$BAK/$f" "$f"; then log "restored '$f'"; else ok=0; log "restoring '$f' failed"; fi
            elif [ "$rc" = 1 ]; then
              if [ -d "$DST/$f" ] || { [ ! -e "$DST/$f" ] && [ ! -L "$DST/$f" ]; }; then :
              elif rm -f -- "$DST/$f" 2>>"$LOG"; then log "took out '$f' (new in this update)"; fi
            else
              ok=0; log "can't read the backup record - left '$f' as it is"
            fi
          done <"$TOUCHED"
          if [ "$ok" = 1 ]; then rm -rf "$STAGE"; else log "the backup is kept in '$BAK'"; fi
          relaunch_old
        }

        log "MudPlay update: new build '$NEW' -> install '$DST' (exe '$EXE')"

        # Wait (bounded ~60s) for the app to exit so we never overwrite open files.
        for _ in $(seq 1 200); do
          kill -0 "$PID" 2>/dev/null || break
          sleep 0.3
        done
        sleep 0.5
        log "old client exited"

        { mkdir -p "$BAK" && : >"$SAVED" && : >"$TOUCHED"; } 2>>"$LOG" \
          || abort "making the backup folder '$BAK' failed"

        # 1. Back up every file of ours this update replaces, the executable with
        #    them. Nothing in the install folder changes until all of them are safe.
        backup_one() {
          [ -d "$DST/$1" ] && return 0
          if [ -e "$DST/$1" ] || [ -L "$DST/$1" ]; then
            cp -pP -- "$DST/$1" "$BAK/$1" 2>>"$LOG" || abort "backing up '$1' failed"
            printf '%s\n' "$1" >>"$SAVED" || abort "recording the backup of '$1' failed"
          fi
        }
        backup_one "$EXENAME"
        while IFS= read -r f || [ -n "$f" ]; do
          f="${f%$'\r'}"; [ -n "$f" ] || continue
          backup_one "$f"
        done <"$INSTALL"

        # 2. Put every file of the new build in place, the executable last. A file
        #    is recorded as touched only once nothing stands in its way, so a
        #    rollback never takes out what was there before.
        install_one() {
          if [ -d "$DST/$2" ]; then rollback "'$2' is a folder in the install"; fi
          if [ -e "$DST/$2$SUF" ] || [ -L "$DST/$2$SUF" ]; then rollback "'$2$SUF' is in the way"; fi
          if [ -e "$DST/$2" ] || [ -L "$DST/$2" ]; then what=replaced; else what=added; fi
          printf '%s\n' "$2" >>"$TOUCHED" || rollback "recording '$2' failed"
          put "$1" "$2" || rollback "putting '$2' in place failed"
          log "$what '$2'"
        }
        while IFS= read -r f || [ -n "$f" ]; do
          f="${f%$'\r'}"; [ -n "$f" ] || continue
          install_one "$NEW/$f" "$f"
        done <"$INSTALL"
        install_one "$NEWEXE" "$EXENAME"

        chmod +x "$EXE" 2>/dev/null || true
        log "swapped - starting the new build"

        # Relaunch the new build, then clean up: the staged download (STAGE holds the
        # archive, the extracted tree, the list and the backup), the step log, and
        # finally this helper itself. Deleting $0 last is safe — bash keeps its open
        # fd to the (now unlinked) script, so it still reads to EOF and exits 0.
        "$EXE" ${ARGS[@]+"${ARGS[@]}"} </dev/null >/dev/null 2>&1 &
        rm -rf "$STAGE"
        rm -f "$LOG"
        rm -f "$0"
        exit 0
        """;

    // cmd for Windows, started with /d /v:off so no AutoRun command or delayed
    // expansion applies. Values from the environment, as for the posix helper.
    //
    // The executable is swapped by rename, never overwritten. Windows won't let a
    // running .exe be written, but it will let one be renamed — so another client
    // still closing, or one opened mid-update, keeps running the old file, and every
    // launch finds either the old or the complete new MudPlay.exe. Overwriting it
    // in place stalled on the lock and could hand a launch a half-written file,
    // which dies before the crash reporter is up. The renamed-aside copy is the
    // executable's backup; it is deleted here when nothing holds it, else by the
    // next startup (RetiredFiles).
    //
    // A renamed-in executable is a new file: it takes the folder's permissions and
    // loses any set on the old one by hand, which left one user's taskbar shortcut
    // refused ("Windows cannot access the specified device, path, or file"). So the
    // old executable's permissions are saved before the swap and put on the new one.
    //
    // Each list name is handed to a subroutine with call, so no name is expanded
    // inside a parenthesised block. Every del is guarded by a check that its name
    // isn't a folder ("name\"): del given a folder empties it. The text goes out
    // with CRLF line ends: cmd can miss a call or goto label in an LF-only file.
    public static string BuildWindows() => """
        @echo off
        setlocal
        set "PID=%MUDPLAY_SWAP_PID%"
        set "NEW=%MUDPLAY_SWAP_NEW%"
        set "NEWEXE=%MUDPLAY_SWAP_NEWEXE%"
        set "DST=%MUDPLAY_SWAP_DST%"
        set "EXE=%MUDPLAY_SWAP_EXE%"
        set "STAGE=%MUDPLAY_SWAP_STAGE%"
        set "PROFILE=%MUDPLAY_SWAP_PROFILE%"
        set "RECONNECT=%MUDPLAY_SWAP_RECONNECT%"
        set "INSTALL=%MUDPLAY_SWAP_INSTALL%"

        rem Refuse, touching nothing, unless every value is there and the staging
        rem folder is an update's own: it is deleted at the end.
        if not defined PID exit /b 2
        if not defined NEW exit /b 2
        if not defined NEWEXE exit /b 2
        if not defined DST exit /b 2
        if not defined EXE exit /b 2
        if not defined STAGE exit /b 2
        if not defined INSTALL exit /b 2
        if "%STAGE:mudplay-update-=%"=="%STAGE%" exit /b 2
        if not exist "%DST%\" exit /b 2
        if not exist "%STAGE%\" exit /b 2
        if not exist "%INSTALL%" exit /b 2
        if not exist "%NEWEXE%" exit /b 2

        for %%I in ("%EXE%") do set "EXENAME=%%~nxI"
        set "BAK=%STAGE%\backup"
        set "SAVED=%STAGE%\backed-up.list"
        set "TOUCHED=%STAGE%\touched.list"
        set "SUF=.mudplay-update-%RANDOM%%RANDOM%"
        set "OLD=%EXE%.old-%RANDOM%%RANDOM%"
        set "LOG=%~dpn0.log"
        set "ACL=%~dpn0.acl"
        set "FAILLOG=%DST%\MudPlay-update-failed.log"
        rem icacls reads "D:\" as an escaped quote; "D:\." names the same folder.
        set "ACLDIR=%DST%"
        if "%ACLDIR:~-1%"=="\" set "ACLDIR=%ACLDIR%."
        set "WHY="
        set "RESTOREFAILED="
        set "MADENEW="

        rem Carry the session across the restart — same character, and a reconnect
        rem when the client was mid-session. Built outside any parenthesised block so
        rem the second line reads what the first one set without delayed expansion.
        set "ARGS="
        if not "%PROFILE%"=="" set ARGS=--profile "%PROFILE%"
        if not "%RECONNECT%"=="" set ARGS=%ARGS% --reconnect

        >"%LOG%" echo MudPlay update %date% %time%
        >>"%LOG%" echo new build: "%NEW%"
        >>"%LOG%" echo install:   "%DST%"
        >>"%LOG%" echo exe:       "%EXE%"

        :waitloop
        tasklist /FI "PID eq %PID%" 2>nul | find "%PID%" >nul
        if not errorlevel 1 (
          timeout /t 1 /nobreak >nul
          goto waitloop
        )
        timeout /t 1 /nobreak >nul
        >>"%LOG%" echo [%time%] old client exited

        if not exist "%BAK%\" mkdir "%BAK%" >>"%LOG%" 2>&1
        if not exist "%BAK%\" (
          set "WHY=making the backup folder failed"
          goto abort
        )
        type nul >"%SAVED%"
        type nul >"%TOUCHED%"

        rem 1. Back up every file of ours this update replaces. Nothing in the
        rem    install folder changes until all of them are safe.
        for /f "usebackq delims=" %%F in ("%INSTALL%") do call :backup "%%F"
        if defined WHY goto abort

        rem 2. Put every file of the new build in place, the executable last.
        for /f "usebackq delims=" %%F in ("%INSTALL%") do call :install "%%F"
        if defined WHY goto rollback

        rem icacls records the file by name, so the restore below finds the new
        rem executable under the same name in the same folder.
        if exist "%EXE%%SUF%" (
          set "WHY=%EXENAME%%SUF% is in the way"
          goto rollback
        )
        icacls "%EXE%" /save "%ACL%" >>"%LOG%" 2>&1
        set "MADENEW=1"
        copy /y "%NEWEXE%" "%EXE%%SUF%" >>"%LOG%" 2>&1
        if errorlevel 1 (
          if not exist "%EXE%%SUF%\" del /f /q "%EXE%%SUF%" >nul 2>&1
          set "WHY=copying the new %EXENAME% in failed"
          goto rollback
        )
        move /y "%EXE%" "%OLD%" >>"%LOG%" 2>&1
        if errorlevel 1 (
          if not exist "%EXE%%SUF%\" del /f /q "%EXE%%SUF%" >nul 2>&1
          set "WHY=renaming the running %EXENAME% aside failed"
          goto rollback
        )
        move /y "%EXE%%SUF%" "%EXE%" >>"%LOG%" 2>&1
        if errorlevel 1 (
          move /y "%OLD%" "%EXE%" >>"%LOG%" 2>&1
          if errorlevel 1 >>"%LOG%" echo [%time%] FAILED to put the old %EXENAME% back: it is kept as "%OLD%" - rename it to %EXENAME% to run MudPlay
          set "WHY=renaming the new %EXENAME% into place failed"
          goto rollback
        )
        rem Not worth failing a finished swap over: the new build still runs with the
        rem folder's permissions.
        if exist "%ACL%" icacls "%ACLDIR%" /restore "%ACL%" /C >>"%LOG%" 2>&1
        >>"%LOG%" echo [%time%] swapped - starting the new build
        start "" "%EXE%" %ARGS%
        rmdir /s /q "%STAGE%"
        if not exist "%OLD%\" del /f /q "%OLD%" >nul 2>&1
        if not exist "%ACL%\" del /f /q "%ACL%" >nul 2>&1
        if not exist "%LOG%\" del /f /q "%LOG%" >nul 2>&1
        rem Pop the batch context so cmd stops reading this file, then delete it — a
        rem .cmd can't del itself while cmd is still line-reading it.
        (goto) 2>nul & del "%~f0"

        :rollback
        >>"%LOG%" echo [%time%] FAILED: %WHY% - restoring the backup
        rem The new executable's temporary copy is ours to remove only once this run
        rem made it, and only while the executable itself is there: with both renames
        rem failed it is a working copy of the new build.
        if defined MADENEW if exist "%EXE%" if not exist "%EXE%%SUF%\" del /f /q "%EXE%%SUF%" >>"%LOG%" 2>&1
        for /f "usebackq delims=" %%F in ("%TOUCHED%") do call :restore "%%F"
        if defined RESTOREFAILED >>"%LOG%" echo [%time%] the backup is kept in "%BAK%"
        if not defined RESTOREFAILED rmdir /s /q "%STAGE%"
        goto relaunchold

        :abort
        >>"%LOG%" echo [%time%] FAILED: %WHY% - install untouched
        if exist "%STAGE%\" rmdir /s /q "%STAGE%"

        :relaunchold
        rem A failed swap leaves this step log beside the executable for the user to
        rem attach to a bug report; the relaunched client points them at it. Never
        rem written through a link (attribute l) or over a folder (d) of that name.
        >>"%LOG%" echo [%time%] restarting the old build
        set "FAILATTR="
        for %%A in ("%FAILLOG%") do set "FAILATTR=%%~aA"
        set "FAILOK=1"
        if defined FAILATTR if not "%FAILATTR:l=%"=="%FAILATTR%" set "FAILOK="
        if defined FAILATTR if not "%FAILATTR:d=%"=="%FAILATTR%" set "FAILOK="
        if defined FAILOK copy /y "%LOG%" "%FAILLOG%" >nul 2>&1
        if not defined FAILOK >>"%LOG%" echo [%time%] not writing the failure log over the link or folder "%FAILLOG%"
        if not exist "%ACL%\" del /f /q "%ACL%" >nul 2>&1
        start "" "%EXE%" %ARGS%
        exit /b 1

        rem ----- Per-file steps, each called with one name from a list -----------

        rem The executable is never on the list: renaming it aside is its backup.
        :backup
        if defined WHY goto :eof
        if /i "%~1"=="%EXENAME%" goto :eof
        if exist "%DST%\%~1\" goto :eof
        if not exist "%DST%\%~1" goto :eof
        copy /y "%DST%\%~1" "%BAK%\%~1" >>"%LOG%" 2>&1
        if errorlevel 1 (
          set "WHY=backing up %~1 failed"
          goto :eof
        )
        (>>"%SAVED%" echo %~1) || set "WHY=recording the backup of %~1 failed"
        goto :eof

        rem A name is recorded as touched only once nothing stands in its way, so a
        rem rollback never takes out what was there before.
        :install
        if defined WHY goto :eof
        if /i "%~1"=="%EXENAME%" goto :eof
        if exist "%DST%\%~1\" (
          set "WHY=%~1 is a folder in the install"
          goto :eof
        )
        if exist "%DST%\%~1%SUF%" (
          set "WHY=%~1%SUF% is in the way"
          goto :eof
        )
        set "WHAT=added"
        if exist "%DST%\%~1" set "WHAT=replaced"
        (>>"%TOUCHED%" echo %~1) || set "WHY=recording %~1 failed"
        if defined WHY goto :eof
        copy /y "%NEW%\%~1" "%DST%\%~1%SUF%" >>"%LOG%" 2>&1
        if errorlevel 1 (
          if not exist "%DST%\%~1%SUF%\" del /f /q "%DST%\%~1%SUF%" >nul 2>&1
          set "WHY=copying %~1 in failed"
          goto :eof
        )
        move /y "%DST%\%~1%SUF%" "%DST%\%~1" >>"%LOG%" 2>&1
        if errorlevel 1 (
          if not exist "%DST%\%~1%SUF%\" del /f /q "%DST%\%~1%SUF%" >nul 2>&1
          set "WHY=putting %~1 in place failed"
          goto :eof
        )
        >>"%LOG%" echo [%time%] %WHAT% %~1
        goto :eof

        rem A backed-up file is put back; one this update added (it wasn't there
        rem before) is taken out again. When the record of what was backed up can't
        rem be read (findstr errorlevel 2), nothing is taken out and the backup is kept.
        :restore
        findstr /x /l /i /c:"%~1" "%SAVED%" >nul 2>&1
        if errorlevel 2 (
          set "RESTOREFAILED=1"
          >>"%LOG%" echo [%time%] can't read the backup record - left %~1 as it is
          goto :eof
        )
        if errorlevel 1 (
          if exist "%DST%\%~1" if not exist "%DST%\%~1\" call :takeout "%~1"
          goto :eof
        )
        copy /y "%BAK%\%~1" "%DST%\%~1%SUF%" >>"%LOG%" 2>&1
        if not errorlevel 1 move /y "%DST%\%~1%SUF%" "%DST%\%~1" >>"%LOG%" 2>&1
        if errorlevel 1 (
          if not exist "%DST%\%~1%SUF%\" del /f /q "%DST%\%~1%SUF%" >nul 2>&1
          set "RESTOREFAILED=1"
          >>"%LOG%" echo [%time%] restoring %~1 failed
          goto :eof
        )
        >>"%LOG%" echo [%time%] restored %~1
        goto :eof

        :takeout
        if not exist "%DST%\%~1\" del /f /q "%DST%\%~1" >>"%LOG%" 2>&1
        if not exist "%DST%\%~1" >>"%LOG%" echo [%time%] took out %~1 - new in this update
        goto :eof
        """.ReplaceLineEndings("\r\n");
}
