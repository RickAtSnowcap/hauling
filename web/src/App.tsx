import { useState, useEffect } from 'react';
import type { UserInfo } from './types';
import { getMe } from './api';
import LoginPage from './components/LoginPage';
import MainPage from './components/MainPage';
import './App.css';

function App() {
  const [user, setUser] = useState<UserInfo | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    const token = params.get('token');
    if (token) {
      sessionStorage.setItem('hauling_token', token);
      window.history.replaceState({}, '', window.location.pathname);
    }

    getMe().then(u => {
      setUser(u);
      setLoading(false);
    }).catch(() => setLoading(false));
  }, []);

  function handleLogout() {
    sessionStorage.removeItem('hauling_token');
    setUser(null);
  }

  if (loading) return <div className="loading">Loading...</div>;
  if (!user) return <LoginPage />;
  return <MainPage user={user} onLogout={handleLogout} />;
}

export default App;
