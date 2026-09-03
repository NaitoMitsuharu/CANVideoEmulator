package jp.co.canreplay.viewer

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.systemBars
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import jp.co.canreplay.viewer.ui.ViewerColors
import jp.co.canreplay.viewer.ui.ViewerScreen

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            MaterialTheme(
                colorScheme = darkColorScheme(
                    background = ViewerColors.Background,
                    surface = ViewerColors.Surface,
                    primary = ViewerColors.Accent,
                    onBackground = ViewerColors.TextPrimary,
                    onSurface = ViewerColors.TextPrimary,
                )
            ) {
                val model: ViewerViewModel = viewModel()
                val state by model.state.collectAsStateWithLifecycle()

                Surface(
                    color = ViewerColors.Background,
                    modifier = Modifier.windowInsetsPadding(WindowInsets.systemBars),
                ) {
                    ViewerScreen(
                        state = state,
                        onStart = model::start,
                        onStop = model::stop,
                        onSignalFilter = model::setSignalFilter,
                        onRawFilter = model::setRawFilter,
                    )
                }
            }
        }
    }
}
