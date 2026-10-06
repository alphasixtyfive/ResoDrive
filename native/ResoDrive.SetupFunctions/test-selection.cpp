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
    puts("PASS: native Setup selection boundaries (24 cases) and launch suppression (4 cases).");
    return 0;
}
