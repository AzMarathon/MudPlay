namespace MudPlay.Services.Update;

// Generates the tiny external helper that performs the actual install swap AFTER
// this process exits — the running executable and its loaded libraries can't be
// overwritten in place, so the app stages the new build, spawns one of these, and
// quits; the helper waits for the PID to die, replaces the install (keeping a
// backup for rollback), relaunches, and cleans up. On success it removes the staged
// download, the rollback backup, AND itself — no update leftovers survive in temp.
//
// Pure string generation so the scripts can be unit-tested without running them.
// Every path is passed as a positional argument (never interpolated into the body)
// so a path with spaces can't break the script.
public static class SwapScriptBuilder
{
    // bash for Linux/macOS. Args at run time:
    // <pid> <newRoot> <installDir> <exe> <stagingDir> <profileToken> <reconnect>.
    // The last two may be empty — an update from a client with no named profile
    // loaded, or one that wasn't connected, relaunches with no argv.
    public static string BuildPosix() => """
        #!/usr/bin/env bash
        set -u
        PID="$1"; NEW="$2"; DST="$3"; EXE="$4"; STAGE="$5"; PROFILE="$6"; RECONNECT="$7"
        BAK="${DST}.bak"

        # Carry the session across the restart: the same character reopens, and a
        # client that was mid-session dials straight back in. Built as an array so a
        # profile name with spaces survives; the ${ARGS[@]+...} form is what keeps an
        # empty array from tripping `set -u`.
        ARGS=()
        [ -n "$PROFILE" ] && ARGS+=(--profile "$PROFILE")
        [ -n "$RECONNECT" ] && ARGS+=(--reconnect)

        # Wait (bounded ~60s) for the app to exit so we never overwrite open files.
        for _ in $(seq 1 200); do
          kill -0 "$PID" 2>/dev/null || break
          sleep 0.3
        done
        sleep 0.5

        # Back up the current install, then move the new one into place. On any
        # failure, restore the backup and relaunch the OLD build so a botched update
        # never strands the user without a client.
        rm -rf "$BAK"
        if ! mv "$DST" "$BAK"; then
          "$EXE" ${ARGS[@]+"${ARGS[@]}"} >/dev/null 2>&1 &
          exit 1
        fi
        if ! mv "$NEW" "$DST"; then
          rm -rf "$DST"
          mv "$BAK" "$DST"
          "$EXE" ${ARGS[@]+"${ARGS[@]}"} >/dev/null 2>&1 &
          exit 1
        fi
        chmod +x "$EXE" 2>/dev/null || true

        # Relaunch the new build, then clean up: the staged download (STAGE holds the
        # archive + the extracted tree), the rollback backup, and finally this helper
        # itself. Deleting $0 last is safe — bash keeps its open fd to the (now
        # unlinked) script, so it still reads to EOF and exits 0.
        "$EXE" ${ARGS[@]+"${ARGS[@]}"} >/dev/null 2>&1 &
        rm -rf "$STAGE"
        rm -rf "$BAK"
        rm -f "$0"
        exit 0
        """;

    // cmd for Windows. Args at run time:
    // <pid> <newRoot> <installDir> <exe> <stagingDir> <profileToken> <reconnect>.
    // robocopy exit codes 0-7 are success, >=8 is failure.
    //
    // The executable is swapped by rename, never overwritten. Windows won't let a
    // running .exe be written, but it will let one be renamed — so another client
    // still closing, or one opened mid-update, keeps running the old file, and every
    // launch finds either the old or the complete new MudPlay.exe. Overwriting it
    // in place stalled on the lock and could hand a launch a half-written file,
    // which dies before the crash reporter is up. The renamed-aside copy is deleted
    // here when nothing holds it, else by the next startup
    // (UpdatePlatform.DeleteReplacedExecutables).
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
        set "BAK=%DST%.bak"
        set "OLD=%EXE%.old-%RANDOM%%RANDOM%"

        rem Carry the session across the restart — same character, and a reconnect
        rem when the client was mid-session. Built outside any parenthesised block so
        rem the second line reads what the first one set without delayed expansion.
        set "ARGS="
        if not "%PROFILE%"=="" set ARGS=--profile "%PROFILE%"
        if not "%RECONNECT%"=="" set ARGS=%ARGS% --reconnect

        :waitloop
        tasklist /FI "PID eq %PID%" 2>nul | find "%PID%" >nul
        if not errorlevel 1 (
          timeout /t 1 /nobreak >nul
          goto waitloop
        )
        timeout /t 1 /nobreak >nul

        if exist "%BAK%" rmdir /s /q "%BAK%"
        robocopy "%DST%" "%BAK%" /MIR /NFL /NDL /NJH /NJS /NP >nul
        rem Everything but the executable mirrors over; the exe (and any old copy
        rem still held by a running client) is left out of the copy and the purge.
        robocopy "%NEW%" "%DST%" /MIR /XF "%EXENAME%" "%EXENAME%.old-*" /R:5 /W:1 /NFL /NDL /NJH /NJS /NP >nul
        if errorlevel 8 goto rollback
        copy /y "%NEW%\%EXENAME%" "%EXE%.new" >nul
        if errorlevel 1 goto rollback
        move /y "%EXE%" "%OLD%" >nul
        if errorlevel 1 goto rollback
        move /y "%EXE%.new" "%EXE%" >nul
        if errorlevel 1 (
          move /y "%OLD%" "%EXE%" >nul
          goto rollback
        )
        start "" "%EXE%" %ARGS%
        rmdir /s /q "%STAGE%"
        rmdir /s /q "%BAK%"
        del /f /q "%OLD%" >nul 2>&1
        rem Pop the batch context so cmd stops reading this file, then delete it — a
        rem .cmd can't del itself while cmd is still line-reading it.
        (goto) 2>nul & del "%~f0"

        :rollback
        if exist "%EXE%.new" del /f /q "%EXE%.new" >nul 2>&1
        robocopy "%BAK%" "%DST%" /MIR /XF "%EXENAME%" "%EXENAME%.old-*" /R:5 /W:1 /NFL /NDL /NJH /NJS /NP >nul
        start "" "%EXE%" %ARGS%
        exit /b 1
        """;
}
