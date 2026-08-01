/* @refresh reload */
import { render } from "solid-js/web";
import { Router } from "@solidjs/router";
import App from "./app";
import "./styles.css";

render(
  () => (
    <Router>
      <App />
    </Router>
  ),
  document.getElementById("root")!
);
