// NOVA: preserve half-close through the transparent tracking wrapper.
func (c *trackedConn) CloseWrite() error {
	if duplex, ok := c.Conn.(N.WriteCloser); ok {
		return duplex.CloseWrite()
	}
	return c.Close()
}
