#1::MouseClick, left, , , 10
#2::MouseClick, left, , , 20
#3::MouseClick, left, , , 30
#4::MouseClick, left, , , 40
#c::
MouseGetPos, begin_x, begin_y
MouseGetPos, next_x, next_y
while(begin_x = next_x && begin_y = next_y)
{
	MouseClick, left, , , 2
	MouseGetPos, next_x, next_y
}
#x::
MouseGetPos, begin_x, begin_y
MouseGetPos, next_x, next_y
while(begin_x = next_x && begin_y = next_y)
{
	Send {x down}
	Sleep 30
	MouseGetPos, next_x, next_y
}