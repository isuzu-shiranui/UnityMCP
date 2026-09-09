#import <AppKit/AppKit.h>
#include <atomic>
#include <thread>
#include <unistd.h>
#include <cassert>
#include <cstdio>
extern "C" char *UnityMcpDialogsList();
extern "C" char *UnityMcpDialogsPress(const char *, const char *);
extern "C" void UnityMcpDialogsFree(void *);
static NSDictionary *Read(char *raw) {
    assert(raw);
    NSData *data = [[NSString stringWithUTF8String:raw] dataUsingEncoding:NSUTF8StringEncoding];
    UnityMcpDialogsFree(raw);
    return [NSJSONSerialization JSONObjectWithData:data options:0 error:nil];
}
int main() {
    @autoreleasepool {
        [NSApplication sharedApplication];
        [NSApp finishLaunching];
        assert([Read(UnityMcpDialogsList())[@"dialogs"] count] == 0);
        NSAlert *alert = [NSAlert new];
        alert.messageText = @"Unity MCP native test";
        alert.informativeText = @"Localized message: 保存 & café";
        [alert addButtonWithTitle:@"Continue"];
        [alert addButtonWithTitle:@"Cancel"];
        alert.showsSuppressionButton = YES;
        alert.suppressionButton.title = @"Do not ask again";
        [alert layout];
        NSString *lastHandle;
        std::thread worker([&] {
            @autoreleasepool {
                usleep(200000);
                NSDictionary *list = Read(UnityMcpDialogsList());
                NSArray *dialogs = list[@"dialogs"];
                assert(dialogs.count == 1);
                NSDictionary *dialog = dialogs[0];
                assert([dialog[@"message"] containsString:@"保存 & café"]);
                assert([dialog[@"buttons"] count] == 2); // suppression checkbox excluded
                lastHandle = dialog[@"handle"];
                assert([Read(UnityMcpDialogsPress("mac:wrong", "Cancel"))[@"error"] isEqual:@"dialog_not_found"]);
                assert([Read(UnityMcpDialogsPress(lastHandle.UTF8String, "Can"))[@"error"] isEqual:@"button_not_found"]);
                assert([Read(UnityMcpDialogsPress(lastHandle.UTF8String, "Cancel"))[@"pressed"] boolValue]);
            }
        });
        assert([alert runModal] == NSAlertSecondButtonReturn);
        // Let the worker finish return bookkeeping after Cocoa's modal loop exits.
        worker.join();
        assert([Read(UnityMcpDialogsPress(lastHandle.UTF8String, "Cancel"))[@"error"] isEqual:@"dialog_not_found"]);

        // A stalled main loop must cancel a queued click, not execute it later.
        [alert layout];
        [alert.window orderFront:nil];
        NSModalSession session = [NSApp beginModalSessionForWindow:alert.window];
        [NSApp runModalSession:session];
        NSString *handle = Read(UnityMcpDialogsList())[@"dialogs"][0][@"handle"];

        // Reusing the window with different content must invalidate the old handle.
        alert.informativeText = @"A different question in the same window";
        [alert layout];
        NSString *updatedHandle = Read(UnityMcpDialogsList())[@"dialogs"][0][@"handle"];
        assert(![handle isEqual:updatedHandle]);
        assert([Read(UnityMcpDialogsPress(handle.UTF8String, "Cancel"))[@"error"] isEqual:@"dialog_not_found"]);

        // Disabled controls are not actionable, and duplicate titles fail closed.
        alert.buttons[0].enabled = NO;
        NSDictionary *disabled = Read(UnityMcpDialogsList())[@"dialogs"][0];
        assert([disabled[@"buttons"] count] == 1);
        assert([Read(UnityMcpDialogsPress([disabled[@"handle"] UTF8String], "Continue"))[@"error"] isEqual:@"button_not_found"]);
        alert.buttons[0].enabled = YES;
        alert.buttons[0].title = @"Cancel";
        NSString *ambiguousHandle = Read(UnityMcpDialogsList())[@"dialogs"][0][@"handle"];
        assert([Read(UnityMcpDialogsPress(ambiguousHandle.UTF8String, "Cancel"))[@"error"] isEqual:@"ambiguous_button"]);
        alert.buttons[0].title = @"Continue";
        handle = Read(UnityMcpDialogsList())[@"dialogs"][0][@"handle"];
        std::thread timeout([&] {
            @autoreleasepool {
                NSDictionary *result = Read(UnityMcpDialogsPress(handle.UTF8String, "Continue"));
                assert([result[@"error"] isEqual:@"dialog_main_loop_unavailable"]);
            }
        });
        timeout.join(); // deliberately prevent the main run loop from servicing the press
        std::thread busy([] {
            @autoreleasepool {
                assert([Read(UnityMcpDialogsList())[@"error"] isEqual:@"dialog_inspection_busy"]);
            }
        });
        busy.join(); // another request cannot add unbounded queued work while frozen
        for (int i=0; i<5; ++i) CFRunLoopRunInMode((__bridge CFStringRef)NSModalPanelRunLoopMode, .02, true);
        assert([NSApp runModalSession:session] == NSModalResponseContinue);
        [NSApp endModalSession:session];
        [alert.window orderOut:nil];
        puts("PASS: modal worker read/press, Unicode, checkbox exclusion, disabled/ambiguous buttons, changed/stale handles, bounded queue, timeout cancellation");
    }
}
