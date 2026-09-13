namespace MudPlay.Services.Update;

// Generates the tiny external helper that performs the actual install swap AFTER
// this process exits — the running executable and its loaded libraries can't be
// overwritten in place, so the app stages the new build, spawns one of these, and
// quits; the helper waits for the PID to die, replaces the install (keeping a
// backup for rollback), relaunches, and cleans up.
//
// Pure string generation so the scripts can be unit-tested without running them.
// Every path is passed as a positional argument (never interpolated into the body)
// so a path with spaces can't break the script.
public static class SwapScriptBuilder
{
    // bash for Linux/macOS. Args at run time: <pid> <newRoot> <installDir> <exe> <stagingDir>.
    public static string BuildPosix() => """
        #!/usr/bin/env bash
        set -u
        PID="$1"; NEW="$2"; DST="$3"; EXE="$4"; STAGE="$5"
        BAK="${DST}.bak"

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
          "$EXE" >/dev/null 2>&1 &
          exit 1
        fi
        if ! mv "$NEW" "$DST"; then
          rm -rf "$DST"
          mv "$BAK" "$DST"
          "$EXE" >/dev/null 2>&1 &
          exit 1
        fi
        chmod +x "$EXE" 2>/dev/null || true

        # Relaunch the new build, then clean up staging + the backup.
        "$EXE" >/dev/null 2>&1 &
        rm -rf "$STAGE"
        rm -rf "$BAK"
        exit 0
        """;

    // cmd for Windows. Args at run time: <pid> <newRoot> <installDir> <exe> <stagingDir>.
    // robocopy exit codes 0-7 are success, >=8 is failure.
    public static string BuildWindows() => """
        @echo off
        setlocal
        set "PID=%~1"
        set "NEW=%~2"
        set "DST=%~3"
        set "EXE=%~4"
        set "STAGE=%~5"
        set "BAK=%DST%.bak"

        :waitloop
        tasklist /FI "PID eq %PID%" 2>nul | find "%PID%" >nul
        if not errorlevel 1 (
          timeout /t 1 /nobreak >nul
          goto waitloop
        )
        timeout /t 1 /nobreak >nul

        if exist "%BAK%" rmdir /s /q "%BAK%"
        robocopy "%DST%" "%BAK%" /MIR /NFL /NDL /NJH /NJS /NP >nul
        robocopy "%NEW%" "%DST%" /MIR /NFL /NDL /NJH /NJS /NP >nul
        if errorlevel 8 (
          robocopy "%BAK%" "%DST%" /MIR /NFL /NDL /NJH /NJS /NP >nul
          start "" "%EXE%"
          exit /b 1
        )
        start "" "%EXE%"
        rmdir /s /q "%STAGE%"
        rmdir /s /q "%BAK%"
        exit /b 0
        """;
}
