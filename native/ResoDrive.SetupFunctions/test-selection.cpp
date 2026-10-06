#include <windows.h>
#include <stdio.h>
#include "selection.h"

int main()
{
    // Both accepted choices, both maintenance directions, and invalid quiet
    // overrides exercise the same decision used immediately before planning.
    for (LONGLONG installed = 0; installed <= 1; ++installed)
        for (LONGLONG selection = -2; selection <= 3; ++selection)
            for (BOOL changing = FALSE; changing <= TRUE; ++changing)
            {
                BOOL expected = (selection == 0 || selection == 1) && (changing || selection == installed);
                if ((SelectionError(selection, installed, changing) == NULL) != expected)
                {
                    fprintf(stderr, "Selection boundary failed: installed=%lld selected=%lld changing=%d\n", installed, selection, changing);
                    return 1;
                }
            }
    for (LONGLONG suppression = -1; suppression <= 2; ++suppression)
        if ((SuppressLaunchError(suppression) == NULL) != (suppression == 0 || suppression == 1)) return 1;
    struct VisibilityCase { BOOL missing; LONGLONG installed; LONGLONG selected; BOOL visible; };
    const VisibilityCase visibility[] = {
        { FALSE, 0, -1, FALSE }, // Healthy fresh/default standard installation.
        { FALSE, 0, 0, FALSE },
        { FALSE, 0, 1, TRUE },  // Explicit compatibility override.
        { FALSE, 1, -1, TRUE }, // Remembered compatibility installation.
        { FALSE, 1, 0, TRUE },  // Group remains visible when opting back in to CET.
        { FALSE, 1, 1, TRUE },
        { TRUE, 0, -1, TRUE },  // Missing capability never changes the selection.
        { TRUE, 0, 0, TRUE },
    };
    for (const auto& test : visibility)
        if (ShowCompatibilityChoice(test.missing, test.installed, test.selected) != test.visible) return 1;
    puts("PASS: native Setup selection boundaries (24 cases), launch suppression (4 cases), and compatibility visibility (8 cases).");
    return 0;
}
