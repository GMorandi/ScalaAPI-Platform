import { render } from "solid-js/web";
import { Router } from "@solidjs/router";
import App from "./app";
import { AuthProvider } from "./store/auth";
import "./styles.css";

render(
  () => (
    <AuthProvider>
      <Router>
        <App />
      </Router>
    </AuthProvider>
  ),
  document.getElementById("root")!,
);
