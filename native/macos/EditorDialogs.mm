#import <AppKit/AppKit.h>
#import <CoreFoundation/CoreFoundation.h>
#include <atomic>
#include <memory>

// Only this process's Cocoa modal windows/sheets are inspected. All AppKit access
// runs on the main run loop, including NSModalPanelRunLoopMode (not Unity's pump).
namespace {
constexpr NSUInteger MaxTextLength = 4096;
constexpr NSUInteger MaxDialogs = 16;
constexpr int MaxSheetDepth = 8;
constexpr int MaxViewDepth = 16;
constexpr int MaxViewCount = 1024;
constexpr int InspectTimeoutMs = 750;
constexpr int PressTimeoutMs = 3000;

enum class RequestState { Pending, Started, Completed, Cancelled };

struct Request {
    std::atomic<RequestState> state{RequestState::Pending};
    dispatch_semaphore_t done = dispatch_semaphore_create(0);
    __strong NSDictionary *result;
};
NSString *Trim(NSString *s) { return [(s ?: @"") stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet]; }
NSString *Bound(NSString *s) { return s.length <= MaxTextLength ? s : [[s substringToIndex:MaxTextLength] stringByAppendingString:@"…"]; }
NSDictionary *Error(NSString *code) { return @{@"error":code}; }

NSArray<NSWindow *> *Windows() {
    NSMutableArray<NSWindow *> *result = [NSMutableArray array];
    if (NSApp.modalWindow.visible) [result addObject:NSApp.modalWindow];
    for (NSWindow *root in NSApp.orderedWindows) {
        NSWindow *sheet = root.attachedSheet;
        for (int depth = 0; sheet && depth < MaxSheetDepth && result.count < MaxDialogs; ++depth, sheet = sheet.attachedSheet)
            if (sheet.visible && ![result containsObject:sheet]) [result addObject:sheet];
        if (result.count >= MaxDialogs) break;
    }
    return result;
}

void Walk(NSView *view, NSMutableArray<NSString *> *text, NSMutableArray<NSButton *> *buttons, int depth, int &budget) {
    if (!view || view.hidden || budget < 0) return;
    if (depth > MaxViewDepth || --budget < 0) { budget = -1; return; }
    if ([view isKindOfClass:NSButton.class]) {
        NSButton *button = (NSButton *)view;
        // Checkboxes/radios change options; they are not dialog response buttons.
        // Native NSAlert responses are bordered push buttons. Accessibility roles
        // may be AXUnknown until an external client activates accessibility, so
        // they cannot be used here without turning this into a permission-based API.
        if (button.enabled && button.bordered && Trim(button.title).length) [buttons addObject:button];
    } else if ([view isKindOfClass:NSTextField.class]) {
        NSTextField *field = (NSTextField *)view;
        if (!field.editable && Trim(field.stringValue).length) [text addObject:Bound(Trim(field.stringValue))];
    } else if ([view isKindOfClass:NSTextView.class]) {
        NSTextView *field = (NSTextView *)view;
        if (!field.editable && Trim(field.string).length) [text addObject:Bound(Trim(field.string))];
    }
    for (NSView *child in view.subviews) Walk(child, text, buttons, depth + 1, budget);
}

NSDictionary *Describe(NSWindow *window, NSArray<NSButton *> **buttonViews = nullptr) {
    NSMutableArray *text = [NSMutableArray array];
    NSMutableArray<NSButton *> *buttons = [NSMutableArray array];
    int budget = MaxViewCount;
    Walk(window.contentView, text, buttons, 0, budget);
    if (budget < 0) return Error(@"dialog_tree_too_large");
    NSMutableArray *titles = [NSMutableArray array];
    for (NSButton *b in buttons) [titles addObject:Trim(b.title)];
    NSString *title = Trim(window.title);
    // NSAlert commonly leaves its window title empty; its first static label is
    // the alert heading. Do not substitute the document/parent window's title.
    if (!title.length && text.count) title = text.firstObject;
    NSDictionary *content = @{@"title":Bound(title), @"message":Bound([text componentsJoinedByString:@"\n"]), @"buttons":titles};
    static NSMapTable<NSWindow *, NSDictionary *> *identities;
    if (!identities) identities = [NSMapTable weakToStrongObjectsMapTable];
    NSDictionary *old = [identities objectForKey:window];
    // A reused window whose message/buttons change gets a new opaque handle.
    if (![old[@"content"] isEqual:content]) {
        old = @{@"content":content, @"handle":[@"mac:" stringByAppendingString:NSUUID.UUID.UUIDString]};
        [identities setObject:old forKey:window];
    }
    NSMutableDictionary *description = [content mutableCopy];
    description[@"handle"] = old[@"handle"];
    if (buttonViews) *buttonViews = buttons;
    return description;
}

NSDictionary *List() {
    NSMutableArray *dialogs = [NSMutableArray array];
    for (NSWindow *w in Windows()) {
        NSDictionary *d = Describe(w);
        if (d[@"error"]) return d;
        [dialogs addObject:d];
    }
    return @{@"dialogs":dialogs};
}

NSDictionary *Press(NSString *handle, NSString *title) {
    if (![handle hasPrefix:@"mac:"] || !Trim(title).length) return Error(@"dialog_not_found");
    for (NSWindow *w in Windows()) {
        NSArray<NSButton *> *buttons;
        NSDictionary *d = Describe(w, &buttons);
        if (d[@"error"]) return d;
        if (![d[@"handle"] isEqual:handle]) continue;
        NSButton *match = nil;
        for (NSButton *b in buttons) {
            if ([Trim(b.title) caseInsensitiveCompare:Trim(title)] != NSOrderedSame) continue;
            if (match) return Error(@"ambiguous_button");
            match = b;
        }
        if (!match) return Error(@"button_not_found");
        [match performClick:nil];
        return @{@"pressed":@YES};
    }
    return Error(@"dialog_not_found");
}

NSDictionary *OnMain(NSDictionary *(^work)(), int timeoutMs) {
    if (NSThread.isMainThread) return work();
    static std::atomic<bool> busy{false};
    bool idle = false;
    if (!busy.compare_exchange_strong(idle, true)) return Error(@"dialog_inspection_busy");
    auto request = std::make_shared<Request>();
    CFRunLoopPerformBlock(CFRunLoopGetMain(), (__bridge CFTypeRef)@[NSDefaultRunLoopMode, NSModalPanelRunLoopMode, NSEventTrackingRunLoopMode], ^{
        auto pending = RequestState::Pending;
        if (!request->state.compare_exchange_strong(pending, RequestState::Started)) { busy.store(false); return; }
        @try { request->result = work(); }
        @catch (NSException *) { request->result = Error(@"dialog_native_error"); }
        request->state.store(RequestState::Completed);
        busy.store(false);
        dispatch_semaphore_signal(request->done);
    });
    CFRunLoopWakeUp(CFRunLoopGetMain());
    if (dispatch_semaphore_wait(request->done, dispatch_time(DISPATCH_TIME_NOW, int64_t(timeoutMs) * NSEC_PER_MSEC)) == 0)
        return request->result;
    auto pending = RequestState::Pending;
    if (request->state.compare_exchange_strong(pending, RequestState::Cancelled))
        return Error(@"dialog_main_loop_unavailable"); // queued action is cancelled, never clicks later
    if (request->state.load() == RequestState::Completed) return request->result;
    return Error(@"dialog_action_pending"); // action already started; must not retry blindly
}

char *Encode(NSDictionary *result) {
    NSData *data = [NSJSONSerialization dataWithJSONObject:result options:0 error:nil];
    if (!data) return strdup("{\"error\":\"dialog_native_error\"}");
    char *copy = (char *)malloc(data.length + 1);
    if (!copy) return nullptr;
    memcpy(copy, data.bytes, data.length);
    copy[data.length] = 0;
    return copy;
}
}

extern "C" __attribute__((visibility("default"))) char *UnityMcpDialogsList() {
    @autoreleasepool { return Encode(OnMain(^{ return List(); }, InspectTimeoutMs)); }
}
extern "C" __attribute__((visibility("default"))) char *UnityMcpDialogsPress(const char *handle, const char *button) {
    @autoreleasepool {
        NSString *h = handle ? [NSString stringWithUTF8String:handle] : @"";
        NSString *b = button ? [NSString stringWithUTF8String:button] : @"";
        return Encode(OnMain(^{ return Press(h, b); }, PressTimeoutMs));
    }
}
extern "C" __attribute__((visibility("default"))) void UnityMcpDialogsFree(void *value) { free(value); }
