namespace MudPlay.Services.Update;

// Generates the tiny external helper that performs the actual install swap AFTER
// this process exits — the running executable and its loaded libraries can't be
// overwritten in place, so the app stages the new build, spawns one of these, and
// quits; the helper waits for the PID to die, swaps the files in, relaunches, and
// cleans up. On success it removes the staged download, the backup, AND itself — no
// update leftovers survive in temp.
//
// The helper works file by file from the list UpdatePlan wrote: the files of the
// new build, each put in place beside the executable. It deletes nothing in the
// install folder but its own temporary copies, the renamed-aside Windows executable,
// and, when a swap fails, a file it had just added; it never moves, mirrors, purges
// or deletes the folder itself: the folder may be Downloads or the Desktop. Files an
// older release left behind are the new build's to remove (RetiredFiles). Each file
// it replaces is first copied into the backup folder inside the staging folder in
// temp — never beside the install — and put back from there if a later step fails.
//
// Pure string generation.
// Every path is passed as a positional argument (never interpolated into the body)
// so a path with spaces can't break the script. The list entries themselves are
// held by UpdatePlan to plain names with no shell syntax in them.
public static class SwapScriptBuilder
{
    // bash for Linux/macOS. Args at run time:
    // <pid> <newRoot> <installDir> <exe> <stagingDir> <profileToken> <reconnect>
    // <installList>.
    // The profile and reconnect args may be empty — an update from a client with no
    // named profile loaded, or one that wasn't connected, relaunches with no argv.
    //
    // A file goes in by copying it beside its target under a temporary name, then
    // renaming it over the target: a rename replaces an executable another client is
    // still running, where writing into it fails, and no launch ever finds a
    // half-copied file.
    public static string BuildPosix() => """
        #!/usr/bin/env bash
        set -u
        PID="$1"; NEW="$2"; DST="$3"; EXE="$4"; STAGE="$5"; PROFILE="$6"; RECONNECT="$7"
        INSTALL="$8"
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
        # attach to a bug report; the relaunched client points them at it.
        relaunch_old() {
          log "restarting the old build"
          cp -f "$LOG" "$FAILLOG" 2>/dev/null || true
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
        # wasn't there before the update is taken out again. The backup is dropped
        # only once everything is back.
        rollback() {
          log "FAILED: $1 - restoring the backup"
          local ok=1 f
          while IFS= read -r f || [ -n "$f" ]; do
            [ -n "$f" ] || continue
            if grep -qxF -- "$f" "$SAVED"; then
              if put "$BAK/$f" "$f"; then log "restored '$f'"; else ok=0; log "restoring '$f' failed"; fi
            else
              rm -f -- "$DST/$f" 2>>"$LOG" && log "took out '$f' (new in this update)"
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

        # 1. Back up every file of ours this update replaces. Nothing in the install
        #    folder changes until all of them are safe.
        while IFS= read -r f || [ -n "$f" ]; do
          f="${f%$'\r'}"; [ -n "$f" ] || continue
          [ -d "$DST/$f" ] && continue
          if [ -e "$DST/$f" ] || [ -L "$DST/$f" ]; then
            cp -pP -- "$DST/$f" "$BAK/$f" 2>>"$LOG" || abort "backing up '$f' failed"
            printf '%s\n' "$f" >>"$SAVED" || abort "recording the backup of '$f' failed"
          fi
        done <"$INSTALL"

        # 2. Put every file of the new build in place.
        while IFS= read -r f || [ -n "$f" ]; do
          f="${f%$'\r'}"; [ -n "$f" ] || continue
          if [ -e "$DST/$f" ] || [ -L "$DST/$f" ]; then what=replaced; else what=added; fi
          printf '%s\n' "$f" >>"$TOUCHED" || rollback "recording '$f' failed"
          put "$NEW/$f" "$f" || rollback "putting '$f' in place failed"
          log "$what '$f'"
        done <"$INSTALL"

        chmod +x "$EXE" 2>/dev/null || true
        log "swapped - starting the new build"

        # Relaunch the new build, then clean up: the staged download (STAGE holds the
        # archive, the extracted tree, the lists and the backup), the step log, and
        # finally this helper itself. Deleting $0 last is safe — bash keeps its open
        # fd to the (now unlinked) script, so it still reads to EOF and exits 0.
        "$EXE" ${ARGS[@]+"${ARGS[@]}"} </dev/null >/dev/null 2>&1 &
        rm -rf "$STAGE"
        rm -f "$LOG"
        rm -f "$0"
        exit 0
        """;

    // cmd for Windows. Args at run time:
    // <pid> <newRoot> <installDir> <exe> <stagingDir> <profileToken> <reconnect>
    // <installList>.
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
    // inside a parenthesised block. A name is checked for being a folder ("name\")
    // before any copy, move or del: del given a folder empties it. The text goes out
    // with CRLF line ends: cmd can miss a call or goto label in an LF-only file.
    public static string BuildWindows() => """
        @echo off
        setlocal
        set "PID=%~1"
        set "NEW=%~2"
        set "DST=%~3"
        set "EXE=%~4"
        set "EXENAME=%~nx4"
        set "STAGE=%~5"
        set "PROFILE=%~6"
        set "RECONNECT=%~7"
        set "INSTALL=%~8"
        set "BAK=%STAGE%\backup"
        set "SAVED=%STAGE%\backed-up.list"
        set "TOUCHED=%STAGE%\touched.list"
        set "SUF=.mudplay-update-%RANDOM%%RANDOM%"
        set "OLD=%EXE%.old-%RANDOM%%RANDOM%"
        set "LOG=%~dpn0.log"
        set "ACL=%~dpn0.acl"
        set "FAILLOG=%DST%\MudPlay-update-failed.log"
        set "WHY="
        set "RESTOREFAILED="

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
        icacls "%EXE%" /save "%ACL%" >>"%LOG%" 2>&1
        copy /y "%NEW%\%EXENAME%" "%EXE%.new" >>"%LOG%" 2>&1
        if errorlevel 1 (
          set "WHY=copying the new %EXENAME% in failed"
          goto rollback
        )
        move /y "%EXE%" "%OLD%" >>"%LOG%" 2>&1
        if errorlevel 1 (
          set "WHY=renaming the running %EXENAME% aside failed"
          goto rollback
        )
        move /y "%EXE%.new" "%EXE%" >>"%LOG%" 2>&1
        if errorlevel 1 (
          move /y "%OLD%" "%EXE%" >>"%LOG%" 2>&1
          set "WHY=renaming the new %EXENAME% into place failed"
          goto rollback
        )
        rem Not worth failing a finished swap over: the new build still runs with the
        rem folder's permissions.
        if exist "%ACL%" icacls "%DST%" /restore "%ACL%" /C >>"%LOG%" 2>&1
        >>"%LOG%" echo [%time%] swapped - starting the new build
        start "" "%EXE%" %ARGS%
        rmdir /s /q "%STAGE%"
        del /f /q "%OLD%" >nul 2>&1
        del /f /q "%ACL%" >nul 2>&1
        del /f /q "%LOG%" >nul 2>&1
        rem Pop the batch context so cmd stops reading this file, then delete it — a
        rem .cmd can't del itself while cmd is still line-reading it.
        (goto) 2>nul & del "%~f0"

        :rollback
        >>"%LOG%" echo [%time%] FAILED: %WHY% - restoring the backup
        if exist "%EXE%.new" del /f /q "%EXE%.new" >>"%LOG%" 2>&1
        for /f "usebackq delims=" %%F in ("%TOUCHED%") do call :restore "%%F"
        if defined RESTOREFAILED >>"%LOG%" echo [%time%] the backup is kept in "%BAK%"
        if not defined RESTOREFAILED rmdir /s /q "%STAGE%"
        goto relaunchold

        :abort
        >>"%LOG%" echo [%time%] FAILED: %WHY% - install untouched
        if exist "%STAGE%\" rmdir /s /q "%STAGE%"

        :relaunchold
        rem A failed swap leaves this step log beside the executable for the user to
        rem attach to a bug report; the relaunched client points them at it.
        >>"%LOG%" echo [%time%] restarting the old build
        copy /y "%LOG%" "%FAILLOG%" >nul 2>&1
        del /f /q "%ACL%" >nul 2>&1
        start "" "%EXE%" %ARGS%
        exit /b 1

        rem ----- Per-file steps, each called with one name from a list -----------

        rem The executable is left out: renaming it aside is its backup.
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
        >>"%SAVED%" echo %~1
        goto :eof

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
        >>"%TOUCHED%" echo %~1
        copy /y "%NEW%\%~1" "%DST%\%~1%SUF%" >>"%LOG%" 2>&1
        if errorlevel 1 (
          del /f /q "%DST%\%~1%SUF%" >nul 2>&1
          set "WHY=copying %~1 in failed"
          goto :eof
        )
        move /y "%DST%\%~1%SUF%" "%DST%\%~1" >>"%LOG%" 2>&1
        if errorlevel 1 (
          del /f /q "%DST%\%~1%SUF%" >nul 2>&1
          set "WHY=putting %~1 in place failed"
          goto :eof
        )
        >>"%LOG%" echo [%time%] %WHAT% %~1
        goto :eof

        rem A backed-up file is put back; one this update added (it wasn't there
        rem before) is taken out again.
        :restore
        findstr /x /l /i /c:"%~1" "%SAVED%" >nul 2>&1
        if errorlevel 1 (
          if not exist "%DST%\%~1\" del /f /q "%DST%\%~1" >>"%LOG%" 2>&1
          >>"%LOG%" echo [%time%] took out %~1 - new in this update
          goto :eof
        )
        copy /y "%BAK%\%~1" "%DST%\%~1%SUF%" >>"%LOG%" 2>&1
        if not errorlevel 1 move /y "%DST%\%~1%SUF%" "%DST%\%~1" >>"%LOG%" 2>&1
        if errorlevel 1 (
          del /f /q "%DST%\%~1%SUF%" >nul 2>&1
          set "RESTOREFAILED=1"
          >>"%LOG%" echo [%time%] restoring %~1 failed
          goto :eof
        )
        >>"%LOG%" echo [%time%] restored %~1
        goto :eof
        """.ReplaceLineEndings("\r\n");
}
