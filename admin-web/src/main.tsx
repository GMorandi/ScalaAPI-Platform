/* @refresh reload */
import { render } from "solid-js/web";
import { Router } from "@solidjs/router";
import App from "./app";
import { AuthProvider } from "./store/auth";
import { ThemeProvider } from "./store/theme";
import { I18nProvider } from "./store/i18n";
import "./styles.css";

render(
  () => (
    <ThemeProvider>
      <I18nProvider>
        <AuthProvider>
          <Router>
            <App />
          </Router>
        </AuthProvider>
      </I18nProvider>
    </ThemeProvider>
  ),
  document.getElementById("root")!
);
