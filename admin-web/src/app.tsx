import { Route, Navigate } from "@solidjs/router";
import type { JSX } from "solid-js";
import { useAuth } from "./store/auth";
import Layout from "./components/Layout";
import Login from "./pages/Login";
import Dashboard from "./pages/Dashboard";
import AccountsList from "./pages/accounts/List";
import AccountForm from "./pages/accounts/Form";
import GroupsList from "./pages/groups/List";
import GroupForm from "./pages/groups/Form";
import UsersList from "./pages/users/List";
import UserForm from "./pages/users/Form";
import ApiKeysList from "./pages/apikeys/List";
import ApiKeyForm from "./pages/apikeys/Form";
import Config from "./pages/Config";

function Protected(props: { children?: JSX.Element }) {
  const { token } = useAuth();
  if (!token()) return <Navigate href="/login" />;
  return <Layout>{props.children}</Layout>;
}

export default function App() {
  return (
    <>
      <Route path="/login" component={Login} />
      <Route path="/" component={Protected}>
        <Route path="/" component={Dashboard} />
        <Route path="/accounts" component={AccountsList} />
        <Route path="/accounts/new" component={AccountForm} />
        <Route path="/accounts/:id" component={AccountForm} />
        <Route path="/groups" component={GroupsList} />
        <Route path="/groups/new" component={GroupForm} />
        <Route path="/groups/:id" component={GroupForm} />
        <Route path="/users" component={UsersList} />
        <Route path="/users/new" component={UserForm} />
        <Route path="/users/:id" component={UserForm} />
        <Route path="/apikeys" component={ApiKeysList} />
        <Route path="/apikeys/new" component={ApiKeyForm} />
        <Route path="/config" component={Config} />
      </Route>
    </>
  );
}
