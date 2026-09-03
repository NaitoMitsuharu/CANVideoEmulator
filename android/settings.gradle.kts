pluginManagement {
    repositories {
        google {
            content {
                includeGroupByRegex("com\\.android.*")
                includeGroupByRegex("com\\.google.*")
                includeGroupByRegex("androidx.*")
            }
        }
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "CanReplayViewer"

// :candecoder is a plain Kotlin/JVM library so its tests -- the ones that matter
// for signal correctness -- run without an emulator or the Android SDK.
include(":candecoder")

// The Android app needs the SDK; skip it when only the decoder is being built
// (e.g. on a CI box without an Android SDK installed).
if (System.getenv("CANREPLAY_SKIP_ANDROID_APP") == null) {
    include(":app")
}
