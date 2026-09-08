import { Route, Routes } from 'react-router'
import Layout from './components/Layout'
import ProductDetailPage from './pages/ProductDetailPage'
import ProductListPage from './pages/ProductListPage'

export default function App() {
  return (
    <Layout>
      <Routes>
        <Route path="/" element={<ProductListPage />} />
        <Route path="/products/:id" element={<ProductDetailPage />} />
      </Routes>
    </Layout>
  )
}
