import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.compose)
}

val localProperties = Properties().apply {
    val file = rootProject.file("local.properties")
    if (file.exists()) file.inputStream().use { load(it) }
}

android {
    namespace = "jp.co.canreplay.viewer"
    compileSdk = 35

    defaultConfig {
        applicationId = "jp.co.canreplay.viewer"
        minSdk = 34
        targetSdk = 35
        versionCode = 1
        versionName = "1.0.0"
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    buildTypes {
        debug {
            // The bundled CAN fixture exists only here.
            buildConfigField("boolean", "HAS_REPLAY_FIXTURE", "true")
        }
        release {
            buildConfigField("boolean", "HAS_REPLAY_FIXTURE", "false")
            isMinifyEnabled = false
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    kotlin {
        compilerOptions {
            jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_17)
        }
    }

    sourceSets {
        getByName("main") {
            kotlin.srcDirs("src/main/kotlin")
        }
    }

    packaging {
        resources.excludes += setOf("/META-INF/{AL2.0,LGPL2.1}")
    }
}

dependencies {
    implementation(project(":candecoder"))

    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(platform(libs.androidx.compose.bom))
    implementation(libs.androidx.compose.ui)
    implementation(libs.androidx.compose.ui.tooling.preview)
    implementation(libs.androidx.compose.material3)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)

    // A hardware CAN source needs whatever library its bridge uses; add it here.
    // See the "Adding a hardware source" note on CanSource.

    debugImplementation(libs.androidx.compose.ui.tooling)
    testImplementation(libs.junit)
    testImplementation(libs.kotlin.test.junit)
}

/**
 * Signal definitions and the demo timeline come straight from a built Scenario
 * Package, so the app always ships definitions that match a real scenario rather
 * than a hand-maintained copy.
 *
 * The two are staged into *different* asset trees, because they belong in
 * different builds (phase 2 requirement 9):
 *
 *   signals.json  -> both debug and release. It is ~100 kB of decode definitions
 *                    and the app cannot show anything without it.
 *   bus_0.canbin  -> debug only. It is ~1.3 MB of recorded CAN whose only
 *                    purpose is to exercise the UI with no CAN bridge attached.
 *                    Shipping it in a release build would put real vehicle CAN
 *                    inside the exhibition APK and invite the app to display
 *                    values that never came off the bus. In release the frames
 *                    must come from the CAN bridge, always.
 */
val scenarioDir = (localProperties.getProperty("scenarioDir")
    ?: System.getenv("CANREPLAY_SCENARIO_DIR")
    ?: rootProject.file("../Scenarios/rav4_001").absolutePath)

val stageSignalAssets by tasks.registering(Copy::class) {
    description = "Copy signals.json from a Scenario Package (all build types)."
    val source = file(scenarioDir)
    onlyIf { source.isDirectory }
    from(source.resolve("dbc")) {
        include("signals.json")
        into("profiles/toyota_rav4_2017")
    }
    into(layout.buildDirectory.dir("generated/signalAssets"))
}

val stageDemoCanAssets by tasks.registering(Copy::class) {
    description = "Copy a demo CAN timeline from a Scenario Package (debug only)."
    val source = file(scenarioDir)
    onlyIf { source.isDirectory }
    from(source.resolve("can")) {
        include("bus_0.canbin")
        into("demo")
    }
    into(layout.buildDirectory.dir("generated/demoCanAssets"))
}

android.sourceSets.getByName("main").assets.srcDir(
    layout.buildDirectory.dir("generated/signalAssets"))
android.sourceSets.getByName("debug").assets.srcDir(
    layout.buildDirectory.dir("generated/demoCanAssets"))

tasks.named("preBuild") { dependsOn(stageSignalAssets) }
tasks.matching { it.name == "preDebugBuild" }.configureEach {
    dependsOn(stageDemoCanAssets)
}
