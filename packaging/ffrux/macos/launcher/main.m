#import <AppKit/AppKit.h>

// A normal, Spotlight-indexable app entry point. The actual companion stays in
// FFApp's versioned payload, so installing/updating FFApp also updates FFRux.
@interface FFRuxLauncher : NSObject <NSApplicationDelegate>
@property(nonatomic, strong) NSMutableArray<NSURL *> *files;
@property(nonatomic) BOOL opening;
@end

@implementation FFRuxLauncher
- (instancetype)init {
    self = [super init];
    if (self) _files = [NSMutableArray new];
    return self;
}

- (void)applicationDidFinishLaunching:(NSNotification *)notification {
    // Launch Services delivers initial document-open events during startup.
    // Defer opening the companion until those events have been collected.
    [self performSelector:@selector(openCompanion) withObject:nil afterDelay:0];
}

- (void)application:(NSApplication *)application openFiles:(NSArray<NSString *> *)filenames {
    for (NSString *filename in filenames) {
        [self.files addObject:[NSURL fileURLWithPath:filename]];
    }
    [application replyToOpenOrPrint:NSApplicationDelegateReplySuccess];
    if (self.opening) [self openCompanion];
}

- (void)showError:(NSString *)message {
    NSAlert *alert = [NSAlert new];
    alert.messageText = @"Could not open FFRux";
    alert.informativeText = message;
    [alert runModal];
    [NSApp terminate:nil];
}

- (void)openCompanion {
    self.opening = YES;
    NSURL *applications = NSBundle.mainBundle.bundleURL.URLByDeletingLastPathComponent;
    NSURL *companion = [applications URLByAppendingPathComponent:@"FFApp.app/Contents/Helpers/FFRux.app"];
    if (![NSFileManager.defaultManager isExecutableFileAtPath:
            [companion URLByAppendingPathComponent:@"Contents/MacOS/FFRux"].path]) {
        [self showError:@"Keep FFApp.app and FFRux.app in the same Applications folder. Reinstall FFApp if its companion is missing."];
        return;
    }
    NSWorkspaceOpenConfiguration *configuration = [NSWorkspaceOpenConfiguration configuration];
    NSArray<NSURL *> *files = self.files.copy;
    [self.files removeAllObjects];
    void (^completed)(NSRunningApplication *, NSError *) = ^(NSRunningApplication *app, NSError *error) {
        dispatch_async(dispatch_get_main_queue(), ^{
            if (error) [self showError:error.localizedDescription];
            else [NSApp terminate:nil];
        });
    };
    if (files.count > 0) {
        [NSWorkspace.sharedWorkspace openURLs:files withApplicationAtURL:companion
            configuration:configuration completionHandler:completed];
    } else {
        [NSWorkspace.sharedWorkspace openApplicationAtURL:companion
            configuration:configuration completionHandler:completed];
    }
}
@end

int main(int argc, const char *argv[]) {
    @autoreleasepool {
        NSApplication *app = NSApplication.sharedApplication;
        // Remain a discoverable APPL bundle, but avoid a second transient Dock icon.
        [app setActivationPolicy:NSApplicationActivationPolicyAccessory];
        __attribute__((objc_precise_lifetime)) FFRuxLauncher *delegate = [FFRuxLauncher new];
        app.delegate = delegate;
        [app run];
    }
    return 0;
}
