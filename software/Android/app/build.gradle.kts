plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

android {
    namespace = "org.unisolder.android"
    compileSdk = 34

    defaultConfig {
        applicationId = "org.unisolder.android"
        minSdk = 26
        targetSdk = 34
        // the CI passes its run number so every build can be installed over the previous one
        versionCode = (project.findProperty("buildNumber") as String?)?.toIntOrNull() ?: 1
        versionName = "1.0"
    }

    // A fixed key (not a secret: it only makes sure builds can be installed over each other)
    signingConfigs {
        getByName("debug") {
            storeFile = rootProject.file("unisolder.keystore")
            storePassword = "android"
            keyAlias = "unisolder"
            keyPassword = "android"
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.getByName("debug")
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions {
        jvmTarget = "17"
    }
}
