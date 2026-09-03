plugins {
    alias(libs.plugins.kotlin.jvm)
}

kotlin {
    jvmToolchain(17)
}

dependencies {
    // org.json is part of Android, but the JVM needs the reference
    // implementation so the same parser code is exercised by the unit tests.
    compileOnly(libs.json)
    testImplementation(libs.json)
    testImplementation(libs.kotlin.test.junit)
    testImplementation(libs.junit)
}

tasks.test {
    useJUnit()
    testLogging {
        events("passed", "failed", "skipped")
    }
}
