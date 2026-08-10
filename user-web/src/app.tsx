import { Navigate, Route } from "@solidjs/router";
import type { JSX } from "solid-js";
import { useAuth } from "./store/auth";
import Layout from "./components/Layout";
import Login from "./pages/Login";
import Register from "./pages/Register";
import Recovery from "./pages/Recovery";
import Verification from "./pages/Verification";
import OAuthCallback from "./pages/OAuthCallback";
import Dashboard from "./pages/Dashboard";
import Keys from "./pages/Keys";
import Usage from "./pages/Usage";
import Billing from "./pages/Billing";
import Profile from "./pages/Profile";
import Security from "./pages/Security";
import Models from "./pages/Models";
import Status from "./pages/Status";
import { Privacy, Terms } from "./pages/Legal";

function Protected(props: { children?: JSX.Element }) {
  const { token } = useAuth();
  return token() ? <Layout>{props.children}</Layout> : <Navigate href="/login" />;
}

export default function App() {
  return <>
    <Route path="/models" component={Models} />
    <Route path="/status" component={Status} />
    <Route path="/terms" component={Terms} />
    <Route path="/privacy" component={Privacy} />
    <Route path="/login" component={Login} />
    <Route path="/register" component={Register} />
    <Route path="/recover" component={Recovery} />
    <Route path="/verify-email" component={Verification} />
    <Route path="/oauth/callback/:provider" component={OAuthCallback} />
    <Route path="/" component={Protected}>
      <Route path="/" component={Dashboard} />
      <Route path="/keys" component={Keys} />
      <Route path="/usage" component={Usage} />
      <Route path="/billing" component={Billing} />
      <Route path="/profile" component={Profile} />
      <Route path="/security" component={Security} />
    </Route>
  </>;
}
