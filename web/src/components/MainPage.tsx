import { useState } from 'react';
import type { UserInfo, OrderDetail } from '../types';
import NewOrder from './NewOrder';
import OrderList from './OrderList';
import './MainPage.css';

interface Props {
  user: UserInfo;
  onLogout: () => void;
}

export default function MainPage({ user, onLogout }: Props) {
  const [tab, setTab] = useState<'new' | 'orders'>('new');
  const [editingOrder, setEditingOrder] = useState<OrderDetail | null>(null);

  const isPrivileged = user.role === 'hauler' || user.role === 'admin';

  function handleEditOrder(order: OrderDetail) {
    setEditingOrder(order);
    setTab('new');
  }

  return (
    <div className="main-page">
      <header className="top-bar">
        <div className="top-left">
          <h1>Angry Hauling</h1>
        </div>
        <div className="top-right">
          <span className="char-name">{user.character_name}</span>
          <span className={`role-badge role-${user.role}`}>{user.role}</span>
          <button className="logout-btn" onClick={onLogout}>Logout</button>
        </div>
      </header>
      <nav className="tab-bar">
        <button className={tab === 'new' ? 'active' : ''} onClick={() => { setEditingOrder(null); setTab('new'); }}>New Order</button>
        <button className={tab === 'orders' ? 'active' : ''} onClick={() => setTab('orders')}>
          {isPrivileged ? 'All Orders' : 'My Orders'}
        </button>
      </nav>
      <div className="tab-content">
        {tab === 'new' && <NewOrder editingOrder={editingOrder} onEditComplete={() => { setEditingOrder(null); setTab('orders'); }} />}
        {tab === 'orders' && <OrderList user={user} onEditOrder={handleEditOrder} />}
      </div>
    </div>
  );
}
